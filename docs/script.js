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
  const pickers = [
    ...document.querySelectorAll(".language-picker, .download-picker"),
  ];
  pickers.forEach((picker) =>
    picker.addEventListener("toggle", () => {
      if (picker.open)
        pickers
          .filter((other) => other !== picker)
          .forEach((other) => {
            other.open = false;
          });
    }),
  );
  document.addEventListener("click", (event) => {
    pickers.forEach((picker) => {
      if (!picker.contains(event.target)) picker.open = false;
    });
  });
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    pickers
      .filter((picker) => picker.open)
      .forEach((picker) => {
        picker.open = false;
        picker.querySelector("summary").focus();
      });
  });
})();
