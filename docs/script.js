(() => {
  "use strict";
  const stages = document.querySelectorAll(".replica-stage");
  const scaleStage = (stage) => {
    if (stage.clientWidth) {
      stage.style.setProperty(
        "--replica-scale",
        stage.clientWidth / Number(stage.dataset.baseWidth),
      );
    }
  };
  const resizeObserver = new ResizeObserver((entries) => {
    entries.forEach(({ target }) => scaleStage(target));
  });
  stages.forEach((stage) => {
    scaleStage(stage);
    resizeObserver.observe(stage);
  });
  const languagePicker = document.querySelector(".language-picker");
  document.addEventListener("click", (event) => {
    if (!languagePicker.contains(event.target)) languagePicker.open = false;
  });
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || !languagePicker.open) return;
    languagePicker.open = false;
    languagePicker.querySelector("summary").focus();
  });
})();
