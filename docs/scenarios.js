(() => {
  "use strict";
  const one = (selector, root = document) => root.querySelector(selector);
  const all = (selector, root = document) => [
    ...root.querySelectorAll(selector),
  ];
  const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
  function pressGroup(selector, active) {
    all(selector).forEach((button) =>
      button.setAttribute("aria-pressed", String(button === active)),
    );
  }
  function makeIcon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "icon");
    svg.setAttribute("aria-hidden", "true");
    const use = document.createElementNS(svg.namespaceURI, "use");
    use.setAttribute("href", `Assets/icons/fluent.svg#${name}`);
    svg.append(use);
    return svg;
  }
  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text) element.textContent = text;
    if (className) element.className = className;
    return element;
  }
  document.addEventListener("click", (event) => {
    const button = event.target.closest("[data-preview]");
    if (button)
      document.dispatchEvent(
        new CustomEvent("demo-preview", { detail: button.dataset.preview }),
      );
  });

  const heroButtons = all("[data-hero-scene]");
  const heroTitleLabel = [...one(".app-tab.active").childNodes].find(
    (child) => child.nodeType === Node.TEXT_NODE && child.textContent.trim(),
  );
  function showHero(scene) {
    one("#hero-browser").hidden = scene !== "browser";
    one("#hero-home").hidden = scene !== "home";
    heroButtons.forEach((button) =>
      button.setAttribute(
        "aria-pressed",
        String(button.dataset.heroScene === scene),
      ),
    );
    one(".showcase-bottom .demo-modes").hidden = scene === "home";
    one(".showcase-bottom .demo-tip").hidden = scene === "home";
    const title = one(".app-tab.active");
    heroTitleLabel.textContent =
      scene === "home" ? " 收藏首页 " : " 灵感与创作 ";
    one("use", title).setAttribute(
      "href",
      `Assets/icons/fluent.svg#${scene === "home" ? "home" : "folder"}`,
    );
  }
  heroButtons.forEach((button) =>
    button.addEventListener("click", () => showHero(button.dataset.heroScene)),
  );
  all("[data-view]").forEach((button) =>
    button.addEventListener("click", () => showHero("browser")),
  );

  const collectionNames = { work: "山野计划", inspiration: "摄影灵感" };
  const sampleFiles = [
    { name: "高山湖泊.jpg", icon: "image", preview: "lake" },
    { name: "创作笔记.md", icon: "document", preview: "notes" },
    { name: "设计简报.pdf", icon: "document", preview: "brief" },
    { name: "项目文件", icon: "folder", preview: "project" },
    { name: "README.md", icon: "code", preview: "readme" },
    { name: "设计归档.zip", icon: "archive", preview: "archive" },
    { name: "灵感收集", icon: "folder", preview: "ideas" },
    { name: "品牌素材", icon: "folder", preview: "brand" },
  ];
  const collectionDialog = one(".collection-dialog");
  let collectionPage = 0;
  let expandedCollection = "work";
  function collectionFiles(key) {
    return key === "inspiration"
      ? [sampleFiles[0], sampleFiles[1], sampleFiles[6]]
      : sampleFiles;
  }
  function fileButton(file) {
    const button = node("button", "", "collection-file");
    button.type = "button";
    button.dataset.preview = file.preview;
    button.title = file.name;
    const art = node("span", "", "collection-art");
    if (file.icon === "image") {
      const img = document.createElement("img");
      img.src = "Assets/alpine-lake.jpg";
      img.alt = "";
      img.width = 1536;
      img.height = 1024;
      art.append(img);
    } else art.append(makeIcon(file.icon));
    button.append(art, node("span", file.name));
    return button;
  }
  function renderExpandedCollection() {
    const files = collectionFiles(expandedCollection);
    const pages = Math.ceil(files.length / 6);
    collectionPage = Math.min(collectionPage, pages - 1);
    one("#collection-dialog-title").textContent =
      `${collectionNames[expandedCollection]} · 收藏夹示例`;
    one(".expanded-grid", collectionDialog).replaceChildren(
      ...files
        .slice(collectionPage * 6, collectionPage * 6 + 6)
        .map(fileButton),
    );
    one("#collection-page").textContent = `${collectionPage + 1} / ${pages}`;
    // Keep paging controls focusable at the ends of the range.
    one("#collection-prev").setAttribute(
      "aria-disabled",
      String(collectionPage === 0),
    );
    one("#collection-next").setAttribute(
      "aria-disabled",
      String(collectionPage === pages - 1),
    );
  }
  all(".collection-demo").forEach((demo) => {
    const title = one(".collection-title", demo);
    const handle = one(".resize-handle", demo);
    function resize(large) {
      demo.dataset.size = large ? "large" : "small";
      const mobile = matchMedia("(max-width: 680px)").matches;
      one(".collection-size", demo).textContent = large
        ? mobile
          ? "展开 · 3 列"
          : "展开 · 4 列"
        : "紧凑 · 3 列";
      one("[data-resize-collection]", demo).textContent = large
        ? "缩小卡片"
        : "调整尺寸";
    }
    all("[data-collection-tab]", demo).forEach((button) =>
      button.addEventListener("click", () => {
        demo.dataset.collection = button.dataset.collectionTab;
        all("[data-collection-tab]", demo).forEach((item) =>
          item.setAttribute("aria-pressed", String(item === button)),
        );
        title.replaceChildren(
          document.createTextNode(
            collectionNames[demo.dataset.collection] + " ",
          ),
          makeIcon("chevron-right"),
        );
        one(".collection-grid", demo).replaceChildren(
          ...collectionFiles(demo.dataset.collection)
            .slice(0, 6)
            .map(fileButton),
        );
      }),
    );
    all("[data-expand-collection]", demo).forEach((button) =>
      button.addEventListener("click", () => {
        expandedCollection = demo.dataset.collection;
        collectionPage = 0;
        renderExpandedCollection();
        collectionDialog.showModal();
      }),
    );
    one("[data-resize-collection]", demo).addEventListener("click", () =>
      resize(demo.dataset.size !== "large"),
    );
    handle.addEventListener("keydown", (event) => {
      if (
        ["ArrowRight", "ArrowDown", "ArrowLeft", "ArrowUp"].includes(event.key)
      ) {
        event.preventDefault();
        resize(["ArrowRight", "ArrowDown"].includes(event.key));
      }
    });
    let dragStart = null;
    let dragged = false;
    handle.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) return;
      dragStart = event.clientX;
      dragged = false;
      handle.setPointerCapture(event.pointerId);
    });
    handle.addEventListener("pointermove", (event) => {
      if (dragStart === null || Math.abs(event.clientX - dragStart) < 16)
        return;
      dragged = true;
      resize(event.clientX > dragStart);
    });
    handle.addEventListener("pointerup", () => {
      dragStart = null;
    });
    handle.addEventListener("pointercancel", () => {
      dragStart = null;
      dragged = false;
    });
    handle.addEventListener("click", () => {
      if (!dragged) resize(demo.dataset.size !== "large");
      dragged = false;
    });
    matchMedia("(max-width: 680px)").addEventListener("change", () =>
      resize(demo.dataset.size === "large"),
    );
  });
  one(".collection-close").addEventListener("click", () =>
    collectionDialog.close(),
  );
  one("#collection-prev").addEventListener("click", () => {
    if (collectionPage > 0) {
      collectionPage--;
      renderExpandedCollection();
    }
  });
  one("#collection-next").addEventListener("click", () => {
    if ((collectionPage + 1) * 6 < collectionFiles(expandedCollection).length) {
      collectionPage++;
      renderExpandedCollection();
    }
  });
  collectionDialog.addEventListener("click", (event) => {
    if (event.target !== collectionDialog) return;
    const r = collectionDialog.getBoundingClientRect();
    if (
      event.clientX < r.left ||
      event.clientX > r.right ||
      event.clientY < r.top ||
      event.clientY > r.bottom
    )
      collectionDialog.close();
  });

  const searchData = {
    name: {
      hint: "按名称查找，可选当前目录或已配置的全局搜索范围；完整路径可直接定位。",
      chips: ["设计", "笔记", "湖泊"],
      files: [
        {
          name: "设计简报.pdf",
          text: "山野计划 / 文档",
          words: "设计简报.pdf",
          preview: "brief",
          icon: "document",
        },
        {
          name: "设计归档.zip",
          text: "山野计划 / 归档",
          words: "设计归档.zip",
          preview: "archive",
          icon: "archive",
        },
        {
          name: "创作笔记.md",
          text: "山野计划 / 文档",
          words: "创作笔记.md",
          preview: "notes",
          icon: "document",
        },
        {
          name: "高山湖泊.jpg",
          text: "山野计划 / 素材",
          words: "高山湖泊.jpg",
          preview: "lake",
          icon: "image",
        },
      ],
    },
    text: {
      hint: "在已提取的图片与 PDF 文字中查找。文字 PDF 直接提取，扫描页使用 OCR；需开启分析并浏览文件夹建立索引。",
      chips: ["山野", "交付", "湖泊"],
      files: [
        {
          name: "设计简报.pdf",
          text: "提取文字：山野计划，交付湖泊主题的视觉素材。",
          words: "山野计划 交付 湖泊 视觉素材",
          preview: "brief",
          icon: "document",
        },
        {
          name: "灵感便签.png",
          text: "图片 OCR：山野计划，周五整理照片。",
          words: "山野计划 周五 整理照片",
          preview: "memo",
          icon: "image",
        },
      ],
    },
    image: {
      hint: "Apple Vision 在设备上分析图片，按人物、场景与文字分类。拍摄日期和地点来自照片元数据，地点名称解析可能需要联网。",
      chips: ["湖泊", "山野", "人物"],
      files: [
        {
          name: "高山湖泊.jpg",
          text: "场景：湖泊、山野 · 日期：2026 年 8 月",
          words: "湖泊 山野 自然",
          preview: "lake",
          icon: "image",
        },
        {
          name: "同行者.jpg",
          text: "人物分类示例 · 旅行素材",
          words: "人物 同行者 旅行",
          preview: "people",
          icon: "image",
        },
      ],
    },
  };
  let searchMode = "name";
  const searchInput = one("#content-search");
  function runSearch() {
    const query = searchInput.value.trim().toLocaleLowerCase();
    const files = searchData[searchMode].files.filter(
      (file) => !query || file.words.toLocaleLowerCase().includes(query),
    );
    one("#result-count").textContent = `${files.length} 项`;
    const buttons = files.map((file) => {
      const button = node("button", "", "search-result");
      button.type = "button";
      button.dataset.preview = file.preview;
      const body = node("span");
      body.append(node("strong", file.name), node("small", file.text));
      button.append(makeIcon(file.icon), body, makeIcon("chevron-right"));
      return button;
    });
    one("#search-results").replaceChildren(
      ...(buttons.length
        ? buttons
        : [
            node("p", "没有匹配的示例文件。试试上方的关键词。", "search-empty"),
          ]),
    );
  }
  function searchModeChanged(mode) {
    searchMode = mode;
    one("#search-scope").textContent = searchData[mode].hint;
    searchInput.value = searchData[mode].chips[0];
    one(".search-chips").replaceChildren(
      ...searchData[mode].chips.map((text) => {
        const button = node("button", text);
        button.type = "button";
        button.dataset.query = text;
        return button;
      }),
    );
    runSearch();
  }
  all("[data-search-mode]").forEach((button) =>
    button.addEventListener("click", () => {
      pressGroup("[data-search-mode]", button);
      searchModeChanged(button.dataset.searchMode);
    }),
  );
  one(".search-chips").addEventListener("click", (event) => {
    const chip = event.target.closest("[data-query]");
    if (chip) {
      searchInput.value = chip.dataset.query;
      runSearch();
    }
  });
  searchInput.addEventListener("input", runSearch);
  runSearch();

  // Deliberately small demonstration renderer. User text is always inserted as text.
  const markdownInput = one("#markdown-input");
  function renderMarkdown() {
    const output = one("#markdown-output");
    output.replaceChildren();
    let list = null;
    for (const line of markdownInput.value.split("\n")) {
      if (!line.trim()) {
        list = null;
        continue;
      }
      if (line.startsWith("- ")) {
        if (!list) {
          list = document.createElement("ul");
          output.append(list);
        }
        list.append(node("li", line.slice(2)));
      } else {
        list = null;
        output.append(
          node(line.startsWith("# ") ? "h3" : "p", line.replace(/^# /, "")),
        );
      }
    }
  }
  markdownInput.addEventListener("input", renderMarkdown);

  const flow = one(".workflow-scene");
  const play = one("#flow-play");
  let flowMode = "delivery",
    step = 0,
    timer = null;
  const flowLabels = {
    delivery: [
      "从菜单栏打开文件速递",
      "选择常用文件",
      "复制拖出，面板收起",
      "复制完成，原文件保留",
    ],
    sftp: [
      "本地与远程目录，并排就位",
      "将素材上传到服务器",
      "使用本地应用编辑文稿",
      "修改自动回传到远程目录",
    ],
  };
  function buttonLabel() {
    play.textContent = reducedMotion.matches
      ? step === 3
        ? "重新开始"
        : "下一步"
      : timer
        ? "暂停"
        : step === 3
          ? "再次播放"
          : step === 0
            ? "播放过程"
            : "继续";
  }
  function stopFlow() {
    clearInterval(timer);
    timer = null;
    buttonLabel();
  }
  function renderFlow() {
    flow.dataset.flowStep = String(step);
    one("#flow-status").textContent = flowLabels[flowMode][step];
    one("#flow-progress").value = step;
    one("#delivery-panel").hidden = flowMode === "delivery" && step >= 2;
    one("#delivery-toggle").setAttribute(
      "aria-expanded",
      String(!one("#delivery-panel").hidden),
    );
    one(".delivery-file").setAttribute("aria-pressed", String(step >= 1));
    one(".received-file").hidden = step !== 3;
    one(".remote-received span").textContent =
      step === 0 ? "等待上传素材" : "高山湖泊.jpg";
    buttonLabel();
  }
  function advance() {
    step = Math.min(step + 1, 3);
    if (step === 3) stopFlow();
    renderFlow();
  }
  function startFlow() {
    if (step === 3) step = 0;
    if (reducedMotion.matches) {
      advance();
      return;
    }
    timer = setInterval(advance, 1600);
    renderFlow();
  }
  play.addEventListener("click", () => {
    if (timer) stopFlow();
    else startFlow();
  });
  one("#flow-reset").addEventListener("click", () => {
    stopFlow();
    step = 0;
    renderFlow();
    startFlow();
  });
  all("[data-workflow]").forEach((button) =>
    button.addEventListener("click", () => {
      stopFlow();
      flowMode = button.dataset.workflow;
      step = 0;
      pressGroup("[data-workflow]", button);
      ["delivery", "sftp"].forEach((mode) => {
        one(`#${mode}-scene`).hidden = flowMode !== mode;
        one(`#${mode}-copy`).hidden = flowMode !== mode;
      });
      renderFlow();
    }),
  );
  one("#delivery-toggle").addEventListener("click", () => {
    stopFlow();
    const panel = one("#delivery-panel");
    panel.hidden = !panel.hidden;
    one("#delivery-toggle").setAttribute(
      "aria-expanded",
      String(!panel.hidden),
    );
  });
  all("[data-delivery-tab]").forEach((button) =>
    button.addEventListener("click", () => {
      stopFlow();
      step = 0;
      renderFlow();
      pressGroup("[data-delivery-tab]", button);
      one(".delivery-path span").textContent =
        button.dataset.deliveryTab === "downloads" ? "下载" : "山野计划";
    }),
  );
  one(".delivery-file").addEventListener("click", () => {
    stopFlow();
    step = 1;
    renderFlow();
  });
  reducedMotion.addEventListener("change", stopFlow);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) stopFlow();
  });
  new IntersectionObserver((entries) => {
    if (!entries[0].isIntersecting) stopFlow();
  }).observe(flow);
  renderFlow();

  all("[data-script-command]").forEach((button) =>
    button.addEventListener("click", () => {
      one("#terminal-command").textContent =
        button.dataset.scriptCommand === "prepare"
          ? '$ /bin/zsh "$SCRIPT" --prepare'
          : '$ /bin/zsh "$SCRIPT" --check';
      one("#terminal-status").textContent = "终端交接示意 · 网页未执行命令";
    }),
  );
})();
