document.documentElement.classList.add("js");

document.addEventListener("htmx:afterSwap", (event) => {
    const target = event.detail.target;
    if (target instanceof HTMLElement) {
        target.classList.remove("is-updating");
    }
});

document.addEventListener("htmx:beforeRequest", (event) => {
    const target = event.detail.target;
    if (target instanceof HTMLElement) {
        target.classList.add("is-updating");
    }
});
