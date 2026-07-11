const url = (name) =>
  ApiClient.getUrl("configurationpage", {
    name,
  });
const tab = (name) => '/configurationpage?name=' + name + '.html';

$(document).ready(() => {
  if (document.querySelector('link[data-jellyfin-xtream-style]')) {
    return;
  }

  const style = document.createElement('link');
  style.rel = 'stylesheet';
  style.href = url('Xtream.css')
  style.dataset.jellyfinXtreamStyle = 'true';
  document.head.appendChild(style);
});

const htmlExpand = document.createElement('span');
htmlExpand.ariaHidden = true;
htmlExpand.classList.add('material-icons', 'expand_more');

const createItemRow = (item, state, update) => {
  const tr = document.createElement('tr');
  tr.dataset['itemId'] = item.Id;

  let td = document.createElement('td');
  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  checkbox.checked = state;
  checkbox.onchange = update;
  td.appendChild(checkbox);
  tr.appendChild(td);

  td = document.createElement('td');
  const label = document.createElement('label');
  label.innerText = item.Name;
  td.appendChild(label);
  tr.appendChild(td);

  td = document.createElement('td');
  if (item.HasCatchup) {
    td.title = `Catch-up supported for ${item.CatchupDuration} days.`;

    let span = document.createElement('span');
    span.innerText = item.CatchupDuration;
    td.appendChild(span);

    span = document.createElement('span');
    span.ariaHidden = true;
    span.classList.add('material-icons', 'timer');
    td.appendChild(span);
  }
  tr.appendChild(td);

  return tr;
}

const populateItemsTable = (wrapper, table, items) => {
  for (let i = 0; i < items.length; ++i) {
    const item = items[i];
    const state = wrapper.live !== undefined && (wrapper.live.length === 0 || wrapper.live.includes(item.Id));
    const row = createItemRow(item, state, (e) => {
      let live = wrapper.live;
      if (e.target.checked) {
        live ??= [];
        live.push(item.Id);
        if (items.every(s => live.includes(s.Id))) {
          live = [];
        }
      } else {
        if (live.length === 0) {
          live = items.map(s => s.Id);
        }
        live = live.filter(id => id != item.Id);
        if (live.length === 0) {
          live = undefined;
        }
      }
      wrapper.live = live;
    });
    table.appendChild(row);
  }
}

const setCheckboxState = (checkbox, live) => {
  checkbox.indeterminate = live !== undefined && live.length > 0;
  checkbox.checked = live !== undefined && live.length === 0;
}

const createCategoryRow = (wrapper, category, loadItems) => {
  const tr = document.createElement('tr');
  tr.dataset['categoryId'] = category.Id;

  let td = document.createElement('td');
  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  setCheckboxState(checkbox, wrapper.live);
  const onchange = () => {
    if (checkbox.checked) {
      wrapper.live = [];
    } else {
      wrapper.live = undefined;
    }
  };
  checkbox.onchange = onchange;
  td.appendChild(checkbox);
  tr.appendChild(td);

  const _wrapper = {
    get live() { return wrapper.live; },
    set live(value) {
      wrapper.live = value;
      setCheckboxState(checkbox, wrapper.live);
    },
  }

  td = document.createElement('td');
  td.textContent = category.Name;
  tr.appendChild(td);

  td = document.createElement('td');
  const expand = document.createElement('button');
  expand.type = 'button';
  expand.classList.add('paper-icon-button-light');
  expand.appendChild(htmlExpand.cloneNode(true));
  expand.onclick = (e) => {
    e.preventDefault();
    const originalClick = expand.onclick;

    Dashboard.showLoadingMsg();
    expand.firstElementChild.classList.replace('expand_more', 'expand_less');
    const table = document.createElement('table');
    loadItems(category.Id)
      .then((items) => populateItemsTable(_wrapper, table, items))
      .catch((error) => {
        console.error('Failed to load Xtream items:', error);
        const message = document.createElement('caption');
        message.textContent = 'Failed to load items. Check the server logs.';
        table.appendChild(message);
      })
      .finally(() => Dashboard.hideLoadingMsg());
    checkbox.onchange = () => {
      onchange();
      table.querySelectorAll('input[type="checkbox"]').forEach((c) => c.checked = checkbox.checked);
    };
    td.appendChild(table);

    expand.onclick = () => {
      expand.onclick = originalClick;

      Dashboard.showLoadingMsg();
      expand.firstElementChild.classList.replace('expand_less', 'expand_more');
      td.removeChild(table);
      Dashboard.hideLoadingMsg();
    };
  };
  td.appendChild(expand);
  tr.appendChild(td);

  return tr;
};

const populateCategoriesTable = (table, loadConfig, loadCategories, loadItems) => {
  Dashboard.showLoadingMsg();
  table.replaceChildren();
  const fetchConfig = loadConfig();
  const fetchCategories = loadCategories();

  return Promise.all([fetchConfig, fetchCategories])
    .then(([config, categories]) => {
      const data = config;
      for (let i = 0; i < categories.length; ++i) {
        const category = categories[i];
        const wrapper = {
          get live() { return data[category.Id]; },
          set live(value) {
            data[category.Id] = value;
          },
        }
        const elem = createCategoryRow(wrapper, category, loadItems);
        table.appendChild(elem);
      }
      Dashboard.hideLoadingMsg();
      return data;
    });
}

const fetchJson = (url) => ApiClient.fetch({
  dataType: 'json',
  type: 'GET',
  url: ApiClient.getUrl(url),
});

const postJson = (url, data) => ApiClient.fetch({
  contentType: 'application/json',
  data: JSON.stringify(data),
  dataType: 'json',
  type: 'POST',
  url: ApiClient.getUrl(url),
});

const setupLegacyStrmMigration = (view, kind) => {
  const previewButton = view.querySelector('#LegacyStrmPreview');
  const quarantineButton = view.querySelector('#LegacyStrmQuarantine');
  const result = view.querySelector('#LegacyStrmResult');
  if (!previewButton || !quarantineButton || !result) {
    return;
  }

  let previewId = null;
  let previewCandidateCount = 0;
  quarantineButton.disabled = true;
  result.textContent = '';
  previewButton.onclick = () => {
    Dashboard.showLoadingMsg();
    previewId = null;
    previewCandidateCount = 0;
    quarantineButton.disabled = true;
    result.textContent = 'Scanning without changing files…';
    fetchJson(`Plugins/JellyfinXtream/v1/LegacyStrmMigration/${kind}`)
      .then((preview) => {
        const count = preview.Candidates?.length || 0;
        if (preview.Truncated) {
          result.textContent = 'The scan reached its safety limit. Narrow the export folder before continuing.';
          return;
        }
        if (preview.Incomplete) {
          result.textContent = `The scan was incomplete because ${preview.SkippedPathCount || 1} path${preview.SkippedPathCount === 1 ? '' : 's'} could not be read. No files can be quarantined until that is fixed.`;
          return;
        }

        previewId = preview.PreviewId;
        previewCandidateCount = count;

        result.textContent = count === 0
          ? 'No high-confidence v0.8 plugin-generated STRM files were found.'
          : `${count} high-confidence legacy file${count === 1 ? '' : 's'} found. No files have been changed.`;
        if (count > 0) {
          const details = document.createElement('details');
          const summary = document.createElement('summary');
          summary.textContent = 'Review every matched path before quarantining';
          details.appendChild(summary);
          const list = document.createElement('ul');
          details.appendChild(list);

          const pageSize = 200;
          const pageCount = Math.ceil(count / pageSize);
          let currentPage = 0;
          const controls = document.createElement('div');
          const previousPage = document.createElement('button');
          previousPage.type = 'button';
          previousPage.textContent = 'Previous paths';
          const pageStatus = document.createElement('span');
          pageStatus.style.margin = '0 12px';
          const nextPage = document.createElement('button');
          nextPage.type = 'button';
          nextPage.textContent = 'Next paths';
          controls.append(previousPage, pageStatus, nextPage);

          const renderPage = () => {
            const start = currentPage * pageSize;
            const end = Math.min(start + pageSize, count);
            list.replaceChildren();
            preview.Candidates.slice(start, end).forEach((candidate) => {
              const item = document.createElement('li');
              item.textContent = candidate.RelativePath;
              list.appendChild(item);
            });
            pageStatus.textContent = `Paths ${start + 1}-${end} of ${count} (page ${currentPage + 1} of ${pageCount})`;
            previousPage.disabled = currentPage === 0;
            nextPage.disabled = currentPage === pageCount - 1;
          };
          previousPage.onclick = () => {
            currentPage--;
            renderPage();
          };
          nextPage.onclick = () => {
            currentPage++;
            renderPage();
          };
          renderPage();
          if (pageCount > 1) {
            details.appendChild(controls);
          }

          result.appendChild(details);
        }

        quarantineButton.disabled = count === 0;
      })
      .catch((error) => {
        console.error('Failed to preview legacy STRM migration:', error);
        result.textContent = 'The preview failed. Save a valid export folder and check the server log.';
      })
      .finally(() => Dashboard.hideLoadingMsg());
  };

  quarantineButton.onclick = () => {
    if (!previewId) {
      result.textContent = 'This preview is no longer available. Preview the files again.';
      return;
    }

    if (!window.confirm(`Move all ${previewCandidateCount} matched legacy STRM files into quarantine? Confirm only after reviewing every page. Manual and unrecognized files will remain in place.`)) {
      return;
    }

    Dashboard.showLoadingMsg();
    quarantineButton.disabled = true;
    postJson(
      `Plugins/JellyfinXtream/v1/LegacyStrmMigration/${kind}/Quarantine`,
      { PreviewId: previewId }
    )
      .then((migration) => {
        previewId = null;
        previewCandidateCount = 0;
        const count = migration.QuarantinedCount || 0;
        const skipped = migration.SkippedCount || 0;
        result.textContent = `${count} legacy file${count === 1 ? '' : 's'} quarantined` +
          (skipped ? `; ${skipped} changed or unsafe file${skipped === 1 ? '' : 's'} skipped.` : '.') +
          (migration.QuarantinePath
            ? ` Batch and migration report: ${migration.QuarantinePath}. Files are retained with a .quarantined suffix for manual recovery.`
            : '');
      })
      .catch((error) => {
        previewId = null;
        previewCandidateCount = 0;
        console.error('Failed to quarantine legacy STRM files:', error);
        result.textContent = 'Quarantine failed or the preview expired. Preview the files again and check the server log.';
      })
      .finally(() => Dashboard.hideLoadingMsg());
  };
};

const filter = (obj, predicate) => Object.keys(obj)
  .filter(key => predicate(obj[key]))
  .reduce((res, key) => (res[key] = obj[key], res), {});

const tabs = [
  {
    href: tab('XtreamCredentials'),
    name: 'Credentials'
  },
  {
    href: tab('XtreamLive'),
    name: 'Live TV'
  },
  {
    href: tab('XtreamLiveOverrides'),
    name: 'TV overrides'
  },
  {
    href: tab('XtreamVod'),
    name: 'Video On-Demand',
  },
  {
    href: tab('XtreamSeries'),
    name: 'Series',
  },
];

const setTabs = (index) => {
  const name = tabs[index].name;
  LibraryMenu.setTabs(name, index, () => tabs);
}

const pluginConfig = {
  UniqueId: '5d774c35-8567-46d3-a950-9bb8227a0c5d'
};

export default {
  fetchJson,
  filter,
  pluginConfig,
  populateCategoriesTable,
  setupLegacyStrmMigration,
  setTabs,
}
