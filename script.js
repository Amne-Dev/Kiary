const revealElements = [...document.querySelectorAll(".reveal")];

const observer = new IntersectionObserver(
  (entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      }
    });
  },
  {
    rootMargin: "0px 0px -12% 0px",
    threshold: 0.14
  }
);

revealElements.forEach((el) => observer.observe(el));

window.addEventListener("load", () => {
  const hero = document.querySelector(".hero .reveal");
  if (hero) {
    hero.classList.add("is-visible");
  }
});
