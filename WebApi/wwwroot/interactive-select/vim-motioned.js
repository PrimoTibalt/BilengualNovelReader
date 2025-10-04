function activateVimScrolling() {
  let scrolling = false;

  const scrollEvent = new Event("keydownvim", { cancelable: true });
  const unscrollEvent = new Event("keyupvim", { cancelable: true });
  document.addEventListener("keydown", (e) => {
    if (e.defaultPrevented) return;

    if (e.key === "j" || e.key === "k") {
      scrollEvent.key = e.key;
      document.dispatchEvent(scrollEvent);
    }
  });
  document.addEventListener("keyup", (e) => {
    if (e.defaultPrevented) return;

    if (e.key === "j" || e.key === "k") {
      document.dispatchEvent(unscrollEvent);
    }
  });

  document.addEventListener("keyupvim", (e) => {
    scrolling = false;
  });
  document.addEventListener("keydownvim", (e) => {
    function scrollSmoothly(scrollDirection) {
      if (!scrolling) return;
      window.scrollBy(0, scrollDirection);
      requestAnimationFrame(() => scrollSmoothly(scrollDirection));
    }

    if (scrolling) return;
    if (e.key === "j") {
      scrolling = true;
      requestAnimationFrame(() => scrollSmoothly(5));
    } else if (e.key === "k") {
      scrolling = true;
      requestAnimationFrame(() => scrollSmoothly(-5));
    }
  });
}

export { activateVimScrolling };