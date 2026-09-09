# Design: Multi-Level (Parent/Child) Namespaces

- **Status**: Implemented. Two deviations from the sketch below are noted inline
  in §5.1 and §5.3.
- **Author**: (fill in)
- **Date**: 2026-09-08
- **Related**: gPAS "Eltern- und Kind-Domänen" (parent/child domains), see
  [gPAS Handbuch v2025.2](https://www.ths-greifswald.de/wp-content/uploads/tools/gpas/2025-12-08_gpas_handbuch_v2025.2.pdf),
  §1.2, §3.1, §4.1.3, §6.1, §6.3, §6.6, Glossar.

## 1. Motivation

Today every vfps namespace is a flat, independent bucket: one `original_value` maps to
one (or, in a multi-psn namespace, several) `pseudonym_value`, and there is no
relationship between namespaces at all. Several real projects need more than one
*level* of pseudonymization for the same underlying subject, e.g.:

- a hospital-wide MPI/PID is pseudonymized once into a project-level pseudonym, and
  that project pseudonym is then re-pseudonymized into a study- or
  data-recipient-specific pseudonym, so a given data recipient never sees a value
  that resolves directly to the MPI;
- the same person needs several parallel second-level pseudonyms (imaging, biomaterial,
  data releases), each independently reversible back down to the first-level
  pseudonym, but not laterally to one another.

gPAS supports this via **Eltern-Domänen** (parent domains) and **Kind-Domänen** (child
domains): domains form a hierarchy, and a child domain's `original_value` is a
pseudonym produced by its parent domain. This document proposes the equivalent for
vfps's namespace concept, scoped to what fits vfps's existing architecture.

## 2. How gPAS actually models this

Summarized from the handbook (translated/paraphrased) to make sure we're building the
same mental model, not a guess at one:

- **A domain** holds its own pseudonym-generation configuration (alphabet, length,
  prefix/suffix, check-digit algorithm, multi-psn flag, expiry). Configuration is
  **not inherited** from a parent — every domain sets its own, independently. (§1.1)
- **Hierarchy**: a domain can have zero, one, or several parent domains
  ("Optional können eine oder mehrere Eltern-Domänen ... angegeben werden"). A domain
  with several parents is shown once in each parent's tree. A domain with no parent
  is a **root domain** — its original values are raw external identifiers (MPI/PID).
  (§1.2, §3.1)
- **What a child domain actually stores**: the *pseudonym value of the parent domain*
  becomes the *original value* fed into the child domain's own pseudonym generation.
  This is why it's called a higher pseudonymization "stage"/"level" — it's pseudonym
  chaining, not namespace grouping. (§1.2, §1.3, Glossar "Originalwert")
- **Sibling domain**: purely a UI convenience — a domain that shares the same
  parent(s) as another domain. Not a distinct relationship in the data model.
- **Optional per-parent-link validation** (`validateValuesViaParents`, §4.1.3, §3.1):
  - `OFF` — no check (default).
  - `VALIDATE` — the value must be well-formed *and* already exist as a pseudonym in
    the parent domain.
  - `ENSURE_EXISTS` — the value must exist as a pseudonym in the parent domain
    (existence only, no format check).
  - `CASCADE_DELETE` — like `ENSURE_EXISTS`, plus: deleting the pseudonym in the
    parent domain deletes the corresponding entry in the child domain too.
- **Manual deletion does not cascade by default.** Deleting a pseudonym pair in one
  domain leaves every other level's entries in place; the deleted value simply
  survives as a dangling original-value (in a lower domain) or pseudonym-value (in a
  higher domain) reference. Only `CASCADE_DELETE` changes that. (§6.6.1)
- **Naming**: a domain has a mutable display **name** and an immutable **key**
  (auto-generated if not given) used to address it over the SOAP API. Renaming only
  changes the UI label; the key can never change.
- **UI**: domains render as a tree (§3.1); right-click a domain to create a sibling or
  a child of it; a per-pseudonym "pseudonymize pseudonym" action creates the
  next-level pseudonym in a chosen child domain (§6.1); a "show tree" action
  visualizes the full chain of linked values across levels for one pseudonym (§6.3).

## 3. Current vfps architecture (baseline)

- `Namespace.Name` is the entity's **primary key** — a plain string, also the API
  identifier used in every URL
  (`/v1/namespaces/{name}`, `/v1/namespaces/{namespace}/pseudonyms`) and in every FK.
  [`Namespace.cs`](../../src/Vfps/Data/Models/Namespace.cs)
- Namespaces are **immutable once created**: there is no Update RPC anywhere in
  `namespaces.proto`, only Create/Get/GetAll/Delete. `CachingNamespaceRepository`
  explicitly relies on this — a cached hit is only ever invalidated by deletion, never
  by an edit.
  [`CachingNamespaceRepository.cs`](../../src/Vfps/Data/CachingNamespaceRepository.cs)
- `Pseudonym`'s key is `(NamespaceName, OriginalValue, SequenceNumber)`; uniqueness of
  an original value, and of a pseudonym value (enforced only via in-memory retry, not
  a DB constraint), is **scoped per namespace** today, with zero cross-namespace
  concept.
  [`Pseudonym.cs`](../../src/Vfps/Data/Models/Pseudonym.cs),
  [`PseudonymContext.cs`](../../src/Vfps/Data/PseudonymContext.cs)
- Namespace `Create`/`Delete` require admin access only; `Get`/pseudonym operations are
  checked against per-namespace access grants, which match **only** by exact namespace
  name or by carrying no namespace at all (the "every namespace" grant) — no notion of
  grouping or hierarchy exists in authorization today. (These grants lived in
  `AuthorizationConfig.NamespaceRules` when this document was written; they have since
  moved into the database and the admin UI, with the same exact-name-or-all matching.)
  [`NamespacePermissionChecker.cs`](../../src/Vfps/Authorization/NamespacePermissionChecker.cs)
- Deleting a namespace cascades to its pseudonyms via a plain Postgres
  `ON DELETE CASCADE` FK. **There is no per-pseudonym delete operation at all** —
  `pseudonyms.proto` only has `Create`/`Get`/`List`.
  [`pseudonyms.proto`](../../src/Vfps/Protos/vfps/api/v1/pseudonyms.proto)
- An original value is already validated against the namespace's optional
  `OriginalValueValidationRegex` before generation, in
  `PseudonymAppService.ValidateOriginalValue` — the natural insertion point for the
  parent-existence check proposed below.
  [`PseudonymAppService.cs`](../../src/Vfps/AppServices/PseudonymAppService.cs)

## 4. Key insight: what's already possible vs. what's actually missing

Pseudonym *chaining* — the core mechanic behind gPAS's hierarchy — already works in
vfps with **zero code changes**: `PseudonymService.Create` takes an arbitrary
`original_value` string for any namespace, so calling `Create` on namespace `study-a`
with `original_value` set to a pseudonym previously generated in namespace `project`
already produces a second-level pseudonym today.

What gPAS adds on top of that raw capability — and what this design is about — is:

1. **Recorded relationship metadata**, so the hierarchy is discoverable and documented
   rather than an informal convention between API callers.
2. **Optional validation** that a child namespace's original value really is a live
   pseudonym in its declared parent, instead of trusting the caller.
3. **Discovery/traversal**: list a namespace's children.
4. **UI affordances**: parent column, "pseudonymize this pseudonym into a child".

Cascade-delete propagation (gPAS's fourth pillar) is deliberately excluded — see
[§6](#6-non-goals).

## 5. Proposed design

### 5.1 Data model

Add two columns to `namespaces`:

```sql
ALTER TABLE namespaces
  ADD COLUMN parent_name text NULL
    REFERENCES namespaces (name) ON DELETE RESTRICT,
  ADD COLUMN parent_validation_mode integer NOT NULL DEFAULT 0;

CREATE INDEX ix_namespaces_parent_name ON namespaces (parent_name);
```

> As implemented, the index is created non-concurrently (EF's default), unlike the
> pseudonym indexes' `IsCreatedConcurrently()`. `namespaces` is a low-cardinality table
> and the same migration already takes a brief exclusive lock to add the columns, so
> there's nothing for a concurrent build to avoid here.

```csharp
public class Namespace : TracksCreationAndUpdates
{
    [Key]
    public required string Name { get; set; }
    // ... existing fields unchanged ...

    /// <summary>
    /// The parent namespace's name, if this namespace is a child in a pseudonymization
    /// hierarchy - i.e. its original values are pseudonyms produced by that parent.
    /// Null means this namespace is a root. Set at creation and never changed
    /// afterwards, like every other field on this entity.
    /// </summary>
    public string? ParentName { get; set; }

    /// <summary>
    /// Whether an original value must already exist as a pseudonym in
    /// <see cref="ParentName"/>'s namespace before a pseudonym is generated for it here.
    /// Only meaningful when <see cref="ParentName"/> is set; off by default.
    /// </summary>
    public ParentValidationMode ParentValidationMode { get; set; }

    public ICollection<Namespace> Children { get; set; } = [];
}
```

Enums are already stored as `integer` in this schema (`pseudonym_generation_method`),
so `parent_validation_mode` follows that precedent. `ParentName` is nullable — every
existing namespace becomes a root, making this a purely additive migration with no
backfill.

**Why an enum rather than a `bool AllowsOnlyParentPseudonyms`**: only two states are in
scope today (off, ensure-exists), but gPAS has four, and two of those (`VALIDATE`,
`CASCADE_DELETE`) are plausible future additions. Adding an enum value later is
additive in proto; widening a `bool` field into an enum is a breaking wire change.

### 5.2 Proto / API changes

`namespaces.proto`:

```protobuf
// whether original values in a child namespace are checked against its parent
enum ParentValidationMode {
  // unspecified performs no parent validation
  PARENT_VALIDATION_MODE_UNSPECIFIED = 0;
  // the original value must already exist as a pseudonym value in the parent
  // namespace, otherwise the request is rejected
  PARENT_VALIDATION_MODE_ENSURE_EXISTS = 1;
}

message Namespace {
  // ... existing fields 1-9 unchanged ...

  // the name of this namespace's parent, if any. Absent means this namespace is a
  // pseudonymization root - its original values are not pseudonyms of another
  // namespace.
  optional string parent_name = 10;
  // how original values are validated against the parent namespace. Only meaningful
  // when parent_name is set.
  ParentValidationMode parent_validation_mode = 11;
}

message NamespaceServiceCreateRequest {
  // ... existing fields 1-8 unchanged ...

  // the name of an existing namespace to set as this namespace's parent. Must
  // already exist. Immutable once the namespace is created.
  optional string parent_name = 9;
  // how original values are validated against the parent namespace. Requires
  // parent_name to be set.
  ParentValidationMode parent_validation_mode = 10;
}

service NamespaceService {
  // ... existing Create/Get/GetAll/Delete unchanged ...

  // list the direct children of a namespace. Non-recursive: callers wanting a full
  // subtree call this per level.
  rpc ListChildren(NamespaceServiceListChildrenRequest)
      returns (NamespaceServiceListChildrenResponse) {
    option (google.api.http) = {get: "/v1/namespaces/{name}/children"};
  }
}

message NamespaceServiceListChildrenRequest {
  // the name of the namespace whose direct children to list
  string name = 1;
}

message NamespaceServiceListChildrenResponse {
  // the direct children of the requested namespace
  repeated Namespace namespaces = 1;
}
```

Namespace names stay flat, globally-unique strings (no `parent/child` path nesting).
Every existing URL is unchanged, so this is strictly additive for existing consumers.

`ListChildren` returns direct children only, unpaginated — consistent with
`GetAll`'s existing "namespace cardinality is expected to stay low" assumption. Like
`GetAllAsync`, results are filtered per-row by the caller's read access.

### 5.3 Business logic

#### Namespace creation (`NamespaceAppService.CreateAsync`)

- If `ParentName` is set: resolve it via `namespaceRepository.FindAsync` and throw
  `NamespaceNotFoundException` if it doesn't exist.
- Reject `ParentName == Name` explicitly with an `ArgumentException`. It can't arise
  today (a namespace can't reference itself before it exists), but the check is cheap
  and guards a future relaxation of immutability.
- Reject `ParentValidationMode != Unspecified` when `ParentName` is null with an
  `ArgumentException` — a validation mode with nothing to validate against is a
  configuration mistake worth failing loudly at creation, matching how an invalid
  regex and a fixed-length/method mismatch are already caught here rather than lazily
  on every later pseudonym create.

Because the parent link is immutable and can only ever point at an
already-existing namespace, **cycles are structurally impossible** and no
cycle-detection logic is needed.

#### Namespace deletion (`NamespaceAppService.DeleteAsync`)

Before deleting, check for children (new `INamespaceRepository.HasChildrenAsync`, or a
filter over `GetAllAsync`). If any exist, throw a new `NamespaceHasChildrenException`
instead of deleting; the caller must delete the children first. The DB-level
`ON DELETE RESTRICT` backs this up so a race can't produce an orphaned pointer — the
app-level check exists to return a clear error rather than let a raw constraint
violation surface. There is no cascade-delete-the-subtree option.

#### Parent-existence validation on pseudonym creation

When a namespace has `ParentValidationMode = EnsureExists`, an original value must
already exist as a *pseudonym value* in the parent namespace:

```sql
SELECT EXISTS (
  SELECT 1 FROM pseudonyms
  WHERE namespace_name = @parentName AND pseudonym_value = @originalValue
)
```

This is covered by the existing `ix_pseudonyms_namespace_name_pseudonym_value` index
([`PseudonymContext.cs`](../../src/Vfps/Data/PseudonymContext.cs)) — no new index
needed. `SequenceNumber` is irrelevant to the lookup, so a multi-psn parent works
without special-casing: any of its stored pseudonyms for any original value is a valid
input to the child.

**Where it runs**: in `PseudonymAppService`, immediately after the existing
`ValidateOriginalValue` regex check and before any generation — cheap in-memory check
first, then the round trip. It applies to *every* create path, including the
`CreateTrustedAsync` overloads: "trusted" there means the permission check was already
done up front by the CSV job runner, not that data-integrity rules are skipped.

**Repository support**:

- As implemented, single-value paths call the same batched method below with a
  one-element list rather than reusing `FindByPseudonymValueAsync` as sketched here —
  one code path instead of two, and existence is checked without materializing a whole
  `Pseudonym` (whose original value has no business being loaded here).
- `CreateTrustedBatchAsync` needs a batched check to avoid one round trip per row. Add:

  ```csharp
  /// <summary>
  /// Returns the subset of <paramref name="pseudonymValues"/> that exist as pseudonym
  /// values in <paramref name="namespaceName"/>. Used by the parent-existence check for
  /// child namespaces, batched so a CSV chunk costs one round trip per parent namespace
  /// rather than one per row.
  /// </summary>
  Task<IReadOnlySet<string>> FilterExistingPseudonymValuesAsync(
      string namespaceName,
      IReadOnlyCollection<string> pseudonymValues,
      CancellationToken cancellationToken
  );
  ```

  A batch's requests may span multiple namespaces, so the app service groups the
  requests by each namespace's `ParentName` (skipping non-validating ones) and issues
  one call per distinct parent.

**Failure mode**: a new exception mirroring the existing
`OriginalValueValidationException` shape:

```csharp
/// <summary>
/// Thrown when a namespace requires its original values to exist as pseudonyms in its
/// parent namespace (<see cref="Namespace.ParentValidationMode"/>) and the given value
/// does not.
/// </summary>
public class ParentPseudonymNotFoundException(string namespaceName, string parentNamespaceName)
    : Exception(
        $"The original value does not exist as a pseudonym in the parent namespace "
            + $"'{parentNamespaceName}' required by namespace '{namespaceName}'."
    )
{
    public string NamespaceName { get; } = namespaceName;
    public string ParentNamespaceName { get; } = parentNamespaceName;
}
```

Note the message deliberately does not echo the original value back — consistent with
the existing validation exceptions, which report the *pattern* rather than the
rejected value.

Mapped in `Services/PseudonymService.cs` to `StatusCode.FailedPrecondition` — the
request is well-formed but the system isn't in a state that permits it, matching how
`MultiplePseudonymsNotAllowedException` is already mapped. (`InvalidArgument`, used
for the regex mismatch, fits less well: the value may be perfectly well-formed and
simply absent upstream.)

**Caching**: `FindByPseudonymValueAsync` is deliberately uncached today, on the
reasoning that reverse lookup is infrequent. This check makes it a hot path for
validating namespaces, so expect one uncached indexed read per create there. If that
shows up in benchmarks, a positive-only cache is safe to add and follows the idiom
already established in `CachingNamespaceRepository`: cache a *hit*, never a *miss* —
a miss must stay uncached because the parent value legitimately appears moments later
(create in parent, then create in child), while a hit can never become stale, since
v1 has no per-pseudonym delete and namespace deletion is blocked while children exist.
Treat this as a follow-up once measured, not part of the initial change.

**CSV jobs**: a validation failure propagates to the job runner's broad
`catch (Exception)` and fails the whole job, which is exactly what happens today for
an `OriginalValueValidationException`. That behavior is intentional here — see
[§7](#7-implementation-decisions).

### 5.4 Authorization

No change to `AuthorizationConfig`/`NamespacePermissionChecker`: grants stay scoped to
an exact namespace name or the existing `"*"` wildcard, with **no implicit inheritance
from a parent namespace to its children**. An admin who wants a role to cover a subtree
lists each namespace explicitly. This keeps authorization resolution static and
explicit rather than adding graph traversal to a security-sensitive check, consistent
with this codebase's preference for explicit, curated config over implicit resolution.

Worth noting as a deliberate consequence: the parent-existence check reads from the
parent namespace on behalf of a caller who may have **no** grant on that parent. This
is intentional and safe — it's an existence check whose result is never returned to
the caller, and a `ParentPseudonymNotFoundException` reveals only that the value the
caller already supplied isn't present upstream. It does not expose parent contents,
and it cannot be used to enumerate them beyond confirming values the caller already
holds.

### 5.5 UI

Minimal for the first iteration:

- `Namespaces.razor`: a "Parent" column in the table, plus a parent-namespace picker
  (existing namespaces only) and a validation-mode toggle in the create form.
- `Pseudonyms.razor`: a "Pseudonymize into..." row action that opens the
  pseudonym-create form for a chosen child namespace with `original_value` pre-filled
  from the selected row — the equivalent of gPAS's "Pseudonymisiere Pseudonym"
  context-menu action (§6.1).

A tree-shaped namespace browser can be layered on later by calling `ListChildren`
per level; hierarchies are expected to be shallow enough that this is not a concern.

### 5.6 CSV jobs / FHIR

No changes needed. Chaining a CSV job's output into a second-level namespace already
works by running a second job against the child namespace with its input column mapped
to the first job's output pseudonym column — an existing capability of
`CsvProcessing`'s column mapping, not a gap. With `EnsureExists` configured on the
child, such a job additionally gets verification that every value it pseudonymizes
really came from the parent.

## 6. Non-goals

- **Multi-parent domains (DAG)** — single parent only. Revisitable later by migrating
  `parent_name` to a `namespace_parents` join table; the single-parent case migrates
  forward cleanly.
- **Config inheritance** from parent to child — each namespace configures its own
  generation method/length/prefix/suffix/regex, which is what gPAS does too.
- **`CASCADE_DELETE`** — excluded, and note it isn't even buildable today: vfps has no
  per-pseudonym delete operation, only whole-namespace delete. Adding one is a separate
  feature with its own authorization and audit questions.
- **gPAS's `VALIDATE` mode** (format check in addition to existence) — vfps has no
  generic "is this a syntactically valid pseudonym for method X" utility to reuse, and
  existence is the check that carries the actual integrity value.
- **Mutable/reassignable parent after creation**, and any namespace Update RPC.
- **Recursive subtree/hierarchy query** or a per-pseudonym cross-level tree view.
- **Depth limits** — hierarchies may be arbitrarily deep.
- **Changes to pseudonym-value uniqueness scoping** — stays per-namespace.
- **gPAS's "anonymization by cutting the link"** — related, but a separate concern.
- **Character sets / check-digit algorithms** — pre-existing unrelated gaps.

## 7. Implementation decisions

Details surfaced by pulling parent validation into scope, resolved during review. No
open questions remain.

1. **CSV job failure granularity** — a parent-existence failure **fails the whole job**,
   matching how an `OriginalValueValidationException` already behaves. Not counted
   per-row alongside `MissingValueCount`/`BadDataRowCount`: a job whose input
   references values that were never pseudonymized upstream is a misconfigured job,
   and failing loudly beats silently pseudonymizing the subset that happened to
   resolve.
2. **`ListChildren` and read access** — children are filtered per-row by read access,
   like `GetAll`, with no partial-result indicator. A caller with access to a parent
   but not to a child therefore sees a parent that appears childless.
3. **A dedicated `ParentValidationMode` enum, not a consolidated feature bitmask.**
   The alternative considered was folding every boolean namespace option into a
   single OR-combined flags enum stored as one integer column. Rejected:
   - `AllowsMultiplePseudonyms` is currently the **only** boolean on `Namespace` —
     a flags container would hold exactly one flag.
   - `ParentValidationMode` is multi-state, not a flag, and needs room for gPAS's
     `VALIDATE`/`CASCADE_DELETE`. Encoding it as bits makes illegal states
     representable (`ENSURE_EXISTS | CASCADE_DELETE` set together); a dedicated enum
     makes them unrepresentable.
   - `allows_multiple_pseudonyms` shipped in **v1.11.0**, so consolidating it is a
     breaking wire change requiring deprecation plus dual-writing — more complexity
     than the per-feature migration it would avoid.
   - It degrades the transcoded JSON/OpenAPI surface this API is built around:
     `"allowsMultiplePseudonyms": true` is self-describing, `"features": 3` forces
     every caller into bit arithmetic. protobuf guidance discourages bitmask enums
     for the same reason — proto3 enums are not flags.
   - In SQL, `WHERE features & 1 = 1` is non-sargable and opaque during ad-hoc
     inspection, versus a plain indexed boolean column.

   Adding a defaulted column per feature is cheap and routine in this repo, and
   namespace cardinality is low enough that compact storage buys nothing. If the
   `Namespace` message later needs structural grouping rather than more flat fields,
   the tool for that is a nested sub-message (e.g. `parent { name, validation_mode }`,
   which would also make "validation mode requires a parent" structural instead of a
   creation-time check) — viable for new fields at any time, since it breaks no
   existing ones. Kept flat here for consistency with today's message shape.

## 8. Scope decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Single parent vs. multi-parent (DAG) | **Single parent** — plain self-referencing FK |
| 2 | Is `parent_name` mutable after creation? | **Immutable**, set at creation only |
| 3 | Parent-existence validation | **In scope** as an opt-in `EnsureExists` mode |
| 4 | Cascade delete | **Not in scope** |
| 5 | Deleting a namespace that has children | **Restrict** — refuse, children first |
| 6 | Authorization inheritance parent → child | **No inheritance**, grants stay explicit |
| 7 | Hierarchy depth limit | **No limit** |
| 8 | Discovery API shape | **Non-recursive `ListChildren`**, called per level |
| 9 | Multi-psn parent interaction | Confirmed — no special-casing needed |
| 10 | UI scope | **Minimal** (parent column, picker, pseudonymize-into-child) |

## 9. Implementation checklist

1. EF Core migration adding `parent_name` (nullable, self-FK, `ON DELETE RESTRICT`,
   indexed) and `parent_validation_mode` (integer, default 0); update
   `PseudonymContextModelSnapshot`.
2. `Namespace` entity + `ParentValidationMode` enum; `PseudonymContext` relationship
   config.
3. Proto: `parent_name`/`parent_validation_mode` on `Namespace` and
   `NamespaceServiceCreateRequest`, `ParentValidationMode` enum, `ListChildren` RPC and
   messages; regenerate.
4. `NamespaceAppService`: parent resolution + validation at create, children check at
   delete, `NamespaceHasChildrenException`; `ListChildrenAsync` with per-row read
   filtering.
5. `INamespaceRepository.HasChildrenAsync` / `ListChildrenAsync` + EF and caching
   implementations.
6. `PseudonymAppService`: parent-existence check on all create paths;
   `ParentPseudonymNotFoundException`; batched check via
   `IPseudonymRepository.FilterExistingPseudonymValuesAsync`.
7. `Services/NamespaceService.cs` + `Services/PseudonymService.cs`: new RPC wiring and
   exception → status-code mappings (`FailedPrecondition`).
8. `InitNamespacesBackgroundService`: ensure config-seeded namespaces carry the parent
   fields, and consider seeding order so a parent is created before its children.
9. UI: parent column + create-form picker in `Namespaces.razor`;
   "pseudonymize into..." action in `Pseudonyms.razor`; German/English resource strings.
10. Tests: namespace create with valid/missing/self parent, validation mode without a
    parent, delete-with-children restriction, pseudonym create under `EnsureExists`
    (present/absent parent value, multi-psn parent), batched validation across
    namespaces, `ListChildren` read filtering.
11. README: document the new fields and `ListChildren`, plus a short hierarchy example.

## 10. References

- gPAS Handbuch v2025.2, §1.1–1.4, §3.1, §4.1.3, §5.1, §6.1/§6.3/§6.6, Glossar.
- [`src/Vfps/Data/Models/Namespace.cs`](../../src/Vfps/Data/Models/Namespace.cs)
- [`src/Vfps/Data/Models/Pseudonym.cs`](../../src/Vfps/Data/Models/Pseudonym.cs)
- [`src/Vfps/Data/PseudonymContext.cs`](../../src/Vfps/Data/PseudonymContext.cs)
- [`src/Vfps/Data/IPseudonymRepository.cs`](../../src/Vfps/Data/IPseudonymRepository.cs)
- [`src/Vfps/Data/CachingPseudonymRepository.cs`](../../src/Vfps/Data/CachingPseudonymRepository.cs)
- [`src/Vfps/Data/CachingNamespaceRepository.cs`](../../src/Vfps/Data/CachingNamespaceRepository.cs)
- [`src/Vfps/Protos/vfps/api/v1/namespaces.proto`](../../src/Vfps/Protos/vfps/api/v1/namespaces.proto)
- [`src/Vfps/Protos/vfps/api/v1/pseudonyms.proto`](../../src/Vfps/Protos/vfps/api/v1/pseudonyms.proto)
- [`src/Vfps/AppServices/NamespaceAppService.cs`](../../src/Vfps/AppServices/NamespaceAppService.cs)
- [`src/Vfps/AppServices/IPseudonymAppService.cs`](../../src/Vfps/AppServices/IPseudonymAppService.cs)
- [`src/Vfps/Config/AuthorizationConfig.cs`](../../src/Vfps/Config/AuthorizationConfig.cs)
- [`src/Vfps/Authorization/NamespacePermissionChecker.cs`](../../src/Vfps/Authorization/NamespacePermissionChecker.cs)
