# CSV processing

vfps processes whole CSV files in the background: it pseudonymizes or de-pseudonymizes one or more
columns, or imports or exports a namespace. Each file processed this way is a _batch_. Start one on
the admin UI's _CSV processing_ page, or
[through the API](#using-the-api). Files are streamed directly to and from S3-compatible object
storage.

![CSV processing page](img/ui/ui-jobs-light.png#only-light)
![CSV processing page](img/ui/ui-jobs-dark.png#only-dark)

A batch's input and output bytes never pass through the vfps process itself: the admin UI, or an
API client, uploads directly to a presigned S3 PUT URL and downloads directly from a presigned S3
GET URL, and a Hangfire background job streams the file S3-to-S3. See `compose.yaml`'s `s3` profile
for a local SeaweedFS setup usable for manual testing.

A batch transforming a file runs in one of two directions: **Pseudonymize** (replace original values with their pseudonym - requires write access to every namespace used) or **De-pseudonymize** (replace pseudonym values with their original value - requires reverse-lookup access, since this reveals data). A pseudonym with no match in its namespace during de-pseudonymization is left unchanged in the output rather than failing the batch.

A file's delimiter must be a single ASCII character or a tab. A batch with any other delimiter is
refused when it's created.

## Unknown values

A **pseudonymizing** batch normally creates a pseudonym for any value the namespace doesn't know yet.
Where that is wrong - a file that is supposed to contain only subjects already in a cohort, where a
new pseudonym would silently record one that was never meant to be there - the **unknown values**
setting changes what happens instead:

- **Fail the batch** stops at the first value the namespace doesn't know, before writing any part of
  the chunk it was found in, and reports the data row, column and namespace it stopped at. The
  value itself is never recorded in the error, only where it was.
- **Keep the original value** leaves each unknown value in the output exactly as the input had it,
  counts them, and carries on - for a file where some rows simply have no counterpart yet, and
  losing the value to a blank cell would be worse. The count is shown on the batch, separately from
  the "missing value" count: both leave the cell as the input had it and mean opposite things - "the
  input had nothing here" versus "the input had a value this namespace has never seen, and it is
  still there".

!!! warning

    The output of a batch with **Keep the original value** is not fully pseudonymized: every value it
    counted is still the original, identifying value, in the very column the batch was asked to
    replace. De-pseudonymization also leaves a value it can't resolve in place, but there the field
    harmlessly stays a pseudonym. Treat the file accordingly, or use **Fail the batch** where that
    isn't acceptable.

The setting applies to pseudonymizing batches only, and is rejected rather than ignored on any other
direction - the other three either read pseudonyms or store pairs the caller already holds, so
there is nothing for it to guard. The equivalent single-value API operation is
`PseudonymService.Resolve`.

## Import and export

The same page also moves a whole namespace in and out as CSV, as two further kinds of batch:

- **Import** loads already-known pairs instead of generating pseudonyms for them - for migrating a
  mapping table that predates vfps, or moving namespaces between instances. The file needs an
  `original` and a `pseudonym` column; when the file has a header row its columns are offered as
  dropdowns, and otherwise a column is named by its 0-based index. Any other column is ignored.
  Requires **write** access to the namespace.

  Optionally a third, **namespace column** can be chosen. Each row is then imported into the
  namespace named in that column, so a whole instance's export loads in one batch rather than one per
  namespace; the single-namespace picker disappears, since there is no longer one to pick. Rows
  naming a namespace that doesn't exist - or leaving the column empty - are reported and skipped
  rather than failing the batch or landing somewhere unintended. This needs **write access to every
  namespace** (an admin, or a grant made without a namespace): the namespaces a file names are only
  known once it is processed, long after the caller who submitted it is gone, so there is no
  narrower permission that can honestly be checked up front.

  Nothing is ever overwritten: an original value that already has a pseudonym keeps it, and a
  pseudonym already in use for a different original value is refused rather than left ambiguous to
  reverse-lookup. Every row - accepted or not - comes back in the batch's downloadable report as the
  input row plus a `status` column (`Imported`, `AlreadyPresent`, `OriginalValueConflict`,
  `PseudonymValueConflict`, `InvalidOriginalValue`, `ParentValueMissing`, `UnknownNamespace`, or
  `MissingValue` for a blank/placeholder cell), so re-running the same file is a clean no-op and a
  partial import is inspectable row by row. A namespace that allows multiple pseudonyms per original value grows
  instead of conflicting, exactly as its generating create path does.

- **Export** writes every original value in a namespace and its pseudonym to a two-column CSV,
  ready to be imported elsewhere. It is the one kind with no file to upload - the batch is
  queued as soon as it is created. Requires **reverse-lookup** access: the output is the
  namespace's original values, in bulk, so it is gated like de-pseudonymization rather than like a
  read.

## Using the API

`BatchService` offers the same batches over gRPC and the JSON-transcoded REST API, under
`/v1/batches`. See the [API reference](api-reference.md#batchservice) for every field.
Like the page, it is only there when object storage is configured - otherwise its routes answer
`404`, and its gRPC methods `UNIMPLEMENTED`.

The file never goes through the API itself. A batch takes two API calls and two plain HTTP
transfers:

1. `POST /v1/batches` creates the batch and returns an `upload` target: a presigned URL, and the
   headers to send with it.
2. `PUT` the file to that URL, straight into object storage.
3. Poll `GET /v1/batches/{id}`. The first poll after the upload notices the file and queues the
   batch - there is no separate "upload finished" call. Once the batch is completed, the response
   carries a `download` target.
4. `GET` the output from that URL.

Using the `development` namespace from the local setup, pseudonymizing a file's `patient_id`
column looks like this:

```sh
VFPS=http://127.0.0.1:8080

printf 'patient_id,age\nP-001,42\nP-002,57\n' > patients.csv

# 1. create the batch
curl -sS -X POST "$VFPS/v1/batches" \
  -H 'content-type: application/json' \
  -d '{
    "originalFileName": "patients.csv",
    "transform": {
      "direction": "BATCH_TRANSFORM_DIRECTION_PSEUDONYMIZE",
      "columnMappings": [{ "sourceColumn": "patient_id", "namespace": "development" }]
    }
  }' > batch.json

BATCH_ID=$(jq -r .batch.id batch.json)

# 2. upload the file, with the header the upload URL was signed with
curl -sS -f -X PUT "$(jq -r .upload.uri batch.json)" \
  -H "Content-Type: $(jq -r '.upload.headers["Content-Type"]' batch.json)" \
  --data-binary @patients.csv

# 3. poll until the batch has finished
while :; do
  curl -sS -f "$VFPS/v1/batches/$BATCH_ID" > batch.json
  STATE=$(jq -r .batch.state batch.json)
  echo "$STATE"
  case "$STATE" in
    BATCH_STATE_AWAITING_UPLOAD | BATCH_STATE_QUEUED | BATCH_STATE_RUNNING) sleep 2 ;;
    *) break ;;
  esac
done

# 4. download the output - only a completed batch has one
curl -sS -f "$(jq -r .download.uri batch.json)" -o patients-pseudonymized.csv
```

`Get` also reports the batch's progress - bytes and rows processed, and the counts of malformed
rows, missing and unresolved values shown on the page. A failed batch says why in `errorMessage`.

Other kinds of batch only differ in the request body:

```json
{ "transform": { "direction": "BATCH_TRANSFORM_DIRECTION_DEPSEUDONYMIZE",
                 "columnMappings": [{ "sourceColumn": "patient_id", "namespace": "development" }] } }

{ "import": { "namespace": "development" } }

{ "export": { "namespace": "development" } }
```

Every kind takes an optional `format` (`encoding`, `delimiter`, `hasHeaderRow`), defaulting to a
UTF-8, comma-separated file with a header row. `encoding` is one of `CSV_ENCODING_UTF_8`,
`CSV_ENCODING_UTF_16_LE`, `CSV_ENCODING_UTF_16_BE`, `CSV_ENCODING_ISO_8859_1`,
`CSV_ENCODING_WINDOWS_1252` and `CSV_ENCODING_US_ASCII` - the same choices the page offers. Pick
Windows-1252 for a file saved as CSV by Excel on Windows. The output is written in the same
format, without a byte-order mark - an input may have one. An export has no file to upload: it is
queued right away, and its response has no `upload` target.

A few things to know:

- **Upload and download URLs are short-lived credentials.** Anyone holding one can use it until it
  expires (`S3__PresignedUrlExpiry`, 15 minutes by default), so don't log them. If a download URL
  expires, call `Get` again for a fresh one. An upload URL can't be renewed - create a new batch.
- **Send the upload headers exactly as given.** They are part of the URL's signature, and object
  storage rejects a `PUT` without them.
- **API clients must be able to reach object storage.** The URLs point at `S3__ServiceUrl`, which
  then has to resolve and be reachable from wherever the client runs, not only from vfps.
- **Access is checked again on every call.** `Get` requires the access the batch's namespaces
  demand, as `Create` did, so revoking a grant also stops its holder from downloading output they
  created earlier.
- **Cleaning up:** `POST /v1/batches/{id}:cancel` stops a batch, and `DELETE /v1/batches/{id}`
  removes a finished one from the list. Its files expire with the bucket's retention period either
  way (`S3__ObjectRetentionDays`). `GET /v1/batches` lists your own batches, or everyone's for an
  admin.

With [access control](access-control.md) on, every call to vfps needs a bearer token, as for any
other API call - add `-H "authorization: Bearer $VFPS_TOKEN"` to the `/v1/batches` requests. The
upload and download URLs are presigned, and need no token.

## Configuration

Batches are off by default. Enabling them, and the object storage they use, is described under
[CSV processing and object storage](configuration.md#csv-processing-and-object-storage) in the configuration
reference.
