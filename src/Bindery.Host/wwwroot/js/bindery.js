document.addEventListener("htmx:beforeRequest", (event) => {
    const target = event.detail.target;
    if (target instanceof HTMLElement) {
        target.classList.add("is-updating");
    }
});

document.addEventListener("htmx:afterSwap", (event) => {
    const target = event.detail.target;
    if (target instanceof HTMLElement) {
        target.classList.remove("is-updating");
    }
});

/*
    The theme control. `theme.js` has already applied the stored preference by the time this
    runs; this only handles changing it.

    "Auto" is the absence of the attribute rather than a third stored value, so a person who
    picks auto goes back to following their OS for good, including when it changes at dusk.
*/
(() => {
    const root = document.documentElement;
    const seg = document.querySelector("[data-theme-seg]");

    if (!seg) {
        return;
    }

    const store = (choice) => {
        try {
            if (choice === "auto") {
                localStorage.removeItem("bindery-theme");
            } else {
                localStorage.setItem("bindery-theme", choice);
            }
        } catch (error) {
            /* Preference is not persisted; the current page still honours it. */
        }
    };

    const announce = () => {
        for (const button of seg.querySelectorAll("[data-theme-set]")) {
            const active = (root.getAttribute("data-theme") ?? "auto") === button.dataset.themeSet;
            button.setAttribute("aria-checked", String(active));
        }
    };

    seg.addEventListener("click", (event) => {
        const button = event.target.closest("[data-theme-set]");
        if (!button) {
            return;
        }

        const choice = button.dataset.themeSet;

        if (choice === "auto") {
            root.removeAttribute("data-theme");
        } else {
            root.setAttribute("data-theme", choice);
        }

        store(choice);
        announce();
    });

    announce();
})();
