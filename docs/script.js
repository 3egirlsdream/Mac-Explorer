(() => {
  "use strict";

  const root = document.documentElement;
  const themeButtons = document.querySelectorAll(".theme-toggle");
  const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
  let explicitTheme = false;
  try {
    explicitTheme = ["light", "dark"].includes(
      localStorage.getItem("mac-explorer-theme"),
    );
  } catch {
    /* The page also works without storage. */
  }

  function updateThemeControls() {
    const dark = root.dataset.theme === "dark";
    themeButtons.forEach((button) => {
      button.setAttribute(
        "aria-label",
        dark ? "切换到浅色外观" : "切换到深色外观",
      );
      button.title = dark ? "切换到浅色外观" : "切换到深色外观";
    });
    document.querySelector('meta[name="theme-color"]').content = dark
      ? "#17191e"
      : "#ffffff";
  }
  themeButtons.forEach((button) =>
    button.addEventListener("click", () => {
      root.dataset.theme = root.dataset.theme === "dark" ? "light" : "dark";
      explicitTheme = true;
      try {
        localStorage.setItem("mac-explorer-theme", root.dataset.theme);
      } catch {
        /* Keep the current in-memory preference. */
      }
      updateThemeControls();
    }),
  );
  systemTheme.addEventListener("change", (event) => {
    if (!explicitTheme) {
      root.dataset.theme = event.matches ? "dark" : "light";
      updateThemeControls();
    }
  });
  updateThemeControls();

  const menuButton = document.querySelector(".menu-toggle");
  const navLinks = document.querySelector(".nav-links");
  function closeMenu() {
    navLinks.classList.remove("is-open");
    menuButton.setAttribute("aria-expanded", "false");
    menuButton.setAttribute("aria-label", "展开导航");
  }
  menuButton.addEventListener("click", () => {
    const open = menuButton.getAttribute("aria-expanded") !== "true";
    navLinks.classList.toggle("is-open", open);
    menuButton.setAttribute("aria-expanded", String(open));
    menuButton.setAttribute("aria-label", open ? "收起导航" : "展开导航");
  });
  navLinks
    .querySelectorAll("a")
    .forEach((link) => link.addEventListener("click", closeMenu));
  document.addEventListener("click", (event) => {
    if (!event.target.closest(".nav")) closeMenu();
  });
  document.addEventListener("keydown", (event) => {
    if (
      event.key === "Escape" &&
      menuButton.getAttribute("aria-expanded") === "true"
    ) {
      closeMenu();
      menuButton.focus();
    }
  });

  const appWindow = document.querySelector(".app-window");
  const viewButtons = document.querySelectorAll("button[data-view]");
  viewButtons.forEach((button) =>
    button.addEventListener("click", () => {
      appWindow.dataset.view = button.dataset.view;
      viewButtons.forEach((item) =>
        item.setAttribute(
          "aria-pressed",
          String(item.dataset.view === button.dataset.view),
        ),
      );
    }),
  );

  const layoutLabels = { two: "左右双窗格", three: "一主两辅", four: "四宫格" };
  const layoutButtons = document.querySelectorAll("button[data-layout]");
  layoutButtons.forEach((button) =>
    button.addEventListener("click", () => {
      const layout = button.dataset.layout;
      const map = document.querySelector(".pane-map");
      map.dataset.layout = layout;
      map.setAttribute("aria-label", `${layoutLabels[layout]}布局示意`);
      document.getElementById("layout-label").textContent =
        layoutLabels[layout];
      layoutButtons.forEach((item) =>
        item.setAttribute("aria-pressed", String(item === button)),
      );
    }),
  );

  const files = {
    brand: { name: "品牌素材", kind: "文件夹", icon: "folder", type: "folder" },
    project: {
      name: "项目文件",
      kind: "文件夹",
      icon: "folder",
      type: "folder",
    },
    lake: {
      name: "高山湖泊.jpg",
      kind: "JPEG 图像 · 2.7 MB",
      icon: "image",
      type: "image",
    },
    notes: {
      name: "创作笔记.md",
      kind: "Markdown 文稿 · 1.2 KB",
      icon: "document",
      type: "document",
      title: "留一点空间，给新的灵感。",
      paragraphs: [
        "把散落的灵感放在一起，把需要专注的项目并排展开。工作区井然有序，思绪也更从容。",
        "这是一份交互演示中的示例文稿。你可以切换视图、搜索文件，或选中示例后按空格预览。",
      ],
    },
    design: {
      name: "界面设计.fig",
      kind: "设计文件 · 8.4 MB",
      icon: "grid",
      type: "unsupported",
    },
    archive: {
      name: "设计归档.zip",
      kind: "ZIP 压缩包 · 12 MB",
      icon: "archive",
      type: "folder",
    },
    readme: {
      name: "README.md",
      kind: "Markdown 文稿 · 2.1 KB",
      icon: "code",
      type: "document",
      title: "Mac Explorer",
      paragraphs: [
        "为 macOS 打造的开源文件管理器。多标签与多窗格、超级预览、搜索和远程管理，集中在一个熟悉的工作区。",
        "此页面展示的是使用示例文件构建的交互演示。完整应用与各版本功能说明，可在项目 GitHub Releases 中查看。",
      ],
    },
    ideas: { name: "灵感收集", kind: "文件夹", icon: "folder", type: "folder" },
  };
  const fileButtons = [...document.querySelectorAll(".file-item")];
  const inspectorArt = document.getElementById("inspector-art");
  const inspectorName = document.getElementById("inspector-name");
  const inspectorKind = document.getElementById("inspector-kind");
  const status = document.getElementById("demo-status");
  let selectedFile = "lake";

  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "icon");
    svg.setAttribute("aria-hidden", "true");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", `Assets/icons/fluent.svg#${name}`);
    svg.append(use);
    return svg;
  }
  function lakeImage() {
    const img = document.createElement("img");
    img.src = "Assets/alpine-lake.jpg";
    img.alt = "湖面倒映雪山，一只红色独木舟停在岸边";
    img.width = 1536;
    img.height = 1024;
    return img;
  }
  function updateStatus() {
    const visible = fileButtons.filter((button) => !button.hidden).length;
    status.textContent = `${visible} 个示例项目${selectedFile ? " · 已选中 1 项" : ""}`;
  }
  function selectFile(id) {
    selectedFile = id;
    fileButtons.forEach((button) => {
      button.classList.toggle("is-selected", button.dataset.file === id);
      button.setAttribute("aria-pressed", String(button.dataset.file === id));
    });
    const file = files[id];
    inspectorName.textContent = file ? file.name : "未选择文件";
    inspectorKind.textContent = file ? file.kind : "选择一个示例文件以预览";
    inspectorArt.replaceChildren(
      file?.type === "image" ? lakeImage() : icon(file?.icon || "document"),
    );
    document.querySelectorAll(".inspector .preview-open").forEach((button) => {
      button.disabled = !file;
    });
    document.querySelector(".inspector-info .file-tag").hidden = !file;
    updateStatus();
  }
  fileButtons.forEach((button) => {
    button.addEventListener("click", () => selectFile(button.dataset.file));
    button.addEventListener("dblclick", () => openPreview(button.dataset.file));
    button.addEventListener("keydown", (event) => {
      if (event.code === "Space") {
        event.preventDefault();
        selectFile(button.dataset.file);
        openPreview(button.dataset.file);
      }
    });
  });
  document.getElementById("demo-search").addEventListener("input", (event) => {
    const query = event.target.value.trim().toLocaleLowerCase();
    fileButtons.forEach((button) => {
      button.hidden = !button.dataset.name.toLocaleLowerCase().includes(query);
    });
    const visible = fileButtons.filter((button) => !button.hidden);
    document.querySelector(".demo-empty").hidden = visible.length > 0;
    if (!visible.some((button) => button.dataset.file === selectedFile))
      selectFile(visible[0]?.dataset.file || null);
    updateStatus();
  });

  const dialog = document.querySelector(".preview-dialog");
  const dialogContent = document.getElementById("dialog-content");
  const dialogTitle = document.getElementById("dialog-title");
  function openPreview(id) {
    const file = files[id];
    if (!file) return;
    dialogTitle.textContent = file.name;
    dialogContent.replaceChildren();
    if (file.type === "image") {
      dialogContent.append(lakeImage());
    } else if (file.type === "document") {
      const article = document.createElement("article");
      article.className = "document-preview";
      const heading = document.createElement("h3");
      heading.textContent = file.title;
      article.append(heading);
      file.paragraphs.forEach((text) => {
        const paragraph = document.createElement("p");
        paragraph.textContent = text;
        article.append(paragraph);
      });
      dialogContent.append(article);
    } else if (file.type === "folder") {
      const content = document.createElement("div");
      content.className = "folder-preview";
      ["lake", "notes"].forEach((childId) => {
        const child = files[childId];
        const button = document.createElement("button");
        button.type = "button";
        button.append(icon(child.icon), document.createTextNode(child.name));
        const hint = document.createElement("small");
        hint.textContent = "点击预览";
        button.append(hint);
        button.addEventListener("click", () => {
          openPreview(childId);
          document.querySelector(".dialog-close").focus();
        });
        content.append(button);
      });
      dialogContent.append(content);
    } else {
      const content = document.createElement("div");
      content.className = "unsupported-preview";
      const heading = document.createElement("h3");
      heading.textContent = file.name;
      const description = document.createElement("p");
      description.textContent =
        "此格式显示文件信息；内容预览取决于系统可用的预览组件。";
      content.append(icon(file.icon), heading, description);
      dialogContent.append(content);
    }
    if (!dialog.open) dialog.showModal();
  }
  document
    .querySelectorAll(".preview-open")
    .forEach((button) =>
      button.addEventListener("click", () => openPreview(selectedFile)),
    );
  document
    .querySelector(".preview-feature-open")
    .addEventListener("click", () => openPreview("archive"));
  document
    .querySelector(".dialog-close")
    .addEventListener("click", () => dialog.close());
  dialog.addEventListener("click", (event) => {
    if (event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    )
      dialog.close();
  });
})();
