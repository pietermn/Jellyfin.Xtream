export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(4);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const visible = view.querySelector("#Visible");
    const strmExportEnabled = view.querySelector("#StrmExportEnabled");
    const strmExportPath = view.querySelector("#StrmExportPath");
    let selectionData;
    getConfig.then((config) => {
      visible.checked = config.IsSeriesVisible;
      strmExportEnabled.checked = config.IsSeriesStrmExportEnabled;
      strmExportPath.value = config.SeriesStrmExportPath || '';
      selectionData = config.Series;
    });
    view.querySelector('#XtreamSeriesForm').onsubmit = (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();
      ApiClient.getPluginConfiguration(pluginId)
        .then((config) => {
          config.IsSeriesVisible = visible.checked;
          config.IsSeriesStrmExportEnabled = strmExportEnabled.checked;
          config.SeriesStrmExportPath = strmExportPath.value;
          if (selectionData !== undefined) {
            config.Series = selectionData;
          }

          return ApiClient.updatePluginConfiguration(pluginId, config);
        })
        .then((result) => Dashboard.processPluginConfigurationUpdateResult(result))
        .catch((error) => {
          console.error('Failed to save series settings:', error);
          Dashboard.hideLoadingMsg();
        });
      return false;
    };
    Xtream.setupLegacyStrmMigration(view, 'series');
    const table = view.querySelector('#SeriesContent');
    Xtream.populateCategoriesTable(
      table,
      () => getConfig.then((config) => config.Series),
      () => Xtream.fetchJson('Plugins/JellyfinXtream/v1/SeriesCategories'),
      (categoryId) => Xtream.fetchJson(`Plugins/JellyfinXtream/v1/SeriesCategories/${categoryId}`),
    ).then((data) => selectionData = data).catch((error) => {
      console.error('Failed to load series categories:', error);
      Dashboard.hideLoadingMsg();
      table.innerHTML = '';
      const errorRow = document.createElement('tr');
      const errorCell = document.createElement('td');
      errorCell.colSpan = 3;
      errorCell.style.color = '#ff6b6b';
      errorCell.style.padding = '16px';
      errorCell.innerHTML = 'Failed to load categories. Please check:<br>' +
        '1. Xtream credentials are configured (Credentials tab)<br>' +
        '2. Xtream server is accessible<br>' +
        '3. Browser console for detailed errors';
      errorRow.appendChild(errorCell);
      table.appendChild(errorRow);
    });
  }));
}
