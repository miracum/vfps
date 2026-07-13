function vfpsToggleDarkMode() {
    document.documentElement.classList.toggle("dark");
    localStorage.setItem(
        "vfps-dark-mode",
        document.documentElement.classList.contains("dark") ? "1" : "0"
    );
}

// Blazor's enhanced navigation patches the live DOM to match each newly-fetched page rather
// than doing a full reload. Since the "dark" class is applied by vfpsApplyTheme() rather than
// being part of the server-rendered markup, that patch strips it back off on every internal
// navigation - reapply it once the patch lands.
Blazor.addEventListener("enhancedload", vfpsApplyTheme);
