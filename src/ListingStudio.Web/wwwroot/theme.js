// Dark mode switch. The server renders <html data-theme> from the ls-theme cookie; with no cookie the page
// follows the device setting through prefers-color-scheme. Clicking the switch saves an explicit choice.
(() => {
    const cookieName = "ls-theme";
    const systemDark = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    const isDark = () => {
        const choice = document.documentElement.getAttribute("data-theme");
        if (choice === "dark") return true;
        if (choice === "light") return false;
        return !!(systemDark && systemDark.matches);
    };

    const syncSwitches = () => {
        const dark = isDark();
        document.querySelectorAll("[data-theme-toggle]").forEach(button => {
            button.setAttribute("aria-checked", dark ? "true" : "false");
            button.setAttribute("title", dark ? "Switch to light mode" : "Switch to dark mode");
        });
    };

    const setTheme = theme => {
        document.documentElement.setAttribute("data-theme", theme);
        document.cookie = `${cookieName}=${theme}; path=/; max-age=31536000; SameSite=Lax${location.protocol === "https:" ? "; Secure" : ""}`;
        syncSwitches();
    };

    document.addEventListener("click", event => {
        const button = event.target instanceof Element ? event.target.closest("[data-theme-toggle]") : null;
        if (!button) return;
        event.preventDefault();
        setTheme(isDark() ? "light" : "dark");
    });

    if (systemDark && systemDark.addEventListener) systemDark.addEventListener("change", syncSwitches);
    document.addEventListener("DOMContentLoaded", syncSwitches);
    syncSwitches();

    // Enhanced navigation and interactive renders replace the header, so refresh the switch state afterwards.
    let attempts = 0;
    const watchBlazor = () => {
        if (window.Blazor && window.Blazor.addEventListener) {
            window.Blazor.addEventListener("enhancedload", syncSwitches);
        } else if (attempts++ < 50) {
            setTimeout(watchBlazor, 100);
        }
    };
    watchBlazor();
})();
