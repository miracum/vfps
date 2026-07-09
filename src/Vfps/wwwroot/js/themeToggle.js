function vfpsToggleDarkMode() {
    document.documentElement.classList.toggle("dark");
    localStorage.setItem(
        "vfps-dark-mode",
        document.documentElement.classList.contains("dark") ? "1" : "0"
    );
}
