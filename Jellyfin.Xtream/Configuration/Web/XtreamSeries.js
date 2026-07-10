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
    getConfig.then((config) => visible.checked = config.IsSeriesVisible);
    const strmExportEnabled = view.querySelector("#StrmExportEnabled");
    getConfig.then((config) => strmExportEnabled.checked = config.IsSeriesStrmExportEnabled);
    const strmExportPath = view.querySelector("#StrmExportPath");
    getConfig.then((config) => strmExportPath.value = config.SeriesStrmExportPath || '');
    const table = view.querySelector('#SeriesContent');
    Xtream.populateCategoriesTable(
      table,
      () => getConfig.then((config) => config.Series),
      () => Xtream.fetchJson('Plugins/JellyfinXtream/v1/SeriesCategories'),
      (categoryId) => Xtream.fetchJson(`Plugins/JellyfinXtream/v1/SeriesCategories/${categoryId}`),
    ).then((data) => {
      view.querySelector('#XtreamSeriesForm').onsubmit = (e) => {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.IsSeriesVisible = visible.checked;
          config.IsSeriesStrmExportEnabled = strmExportEnabled.checked;
          config.SeriesStrmExportPath = strmExportPath.value;
          config.Series = data;
          ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
            Dashboard.processPluginConfigurationUpdateResult(result);
          });
        });

        e.preventDefault();
        return false;
      };
    }).catch((error) => {
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
