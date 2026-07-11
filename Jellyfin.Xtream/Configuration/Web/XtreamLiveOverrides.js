export default function (view) {
  const createChannelRow = (channel, overrides) => {
    const tr = document.createElement('tr');
    tr.dataset['channelId'] = channel.Id;

    let td = document.createElement('td');
    const number = document.createElement('input');
    number.type = 'number';
    number.setAttribute('is', 'emby-input');
    number.placeholder = channel.Number;
    number.value = overrides.Number ?? '';
    number.onchange = () => number.value ?
      overrides.Number = parseInt(number.value, 10) :
      delete overrides.Number;
    td.appendChild(number);
    tr.appendChild(td);

    td = document.createElement('td');
    const name = document.createElement('input');
    name.type = 'text';
    name.setAttribute('is', 'emby-input');
    name.placeholder = channel.Name;
    name.value = overrides.Name ?? '';
    name.onchange = () => name.value ?
      overrides.Name = name.value :
      delete overrides.Name;
    td.appendChild(name);
    tr.appendChild(td);

    td = document.createElement('td');
    const image = document.createElement('input');
    image.type = 'text';
    image.setAttribute('is', 'emby-input');
    image.placeholder = channel.LogoUrl || '';
    image.value = overrides.LogoUrl ?? '';
    image.onchange = () => image.value ?
      overrides.LogoUrl = image.value :
      delete overrides.LogoUrl;
    td.appendChild(image);
    tr.appendChild(td);

    return tr;
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(2);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const table = view.querySelector('#LiveChannels');
    table.replaceChildren();
    let overrideData;
    getConfig.then((config) => overrideData = config.LiveTvOverrides || {});
    view.querySelector('#XtreamLiveOverridesForm').onsubmit = (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();
      ApiClient.getPluginConfiguration(pluginId)
        .then((config) => {
          const data = overrideData ?? config.LiveTvOverrides ?? {};
          config.LiveTvOverrides = Xtream.filter(
            data,
            overrides => Object.keys(overrides).length > 0
          );
          return ApiClient.updatePluginConfiguration(pluginId, config);
        })
        .then((result) => Dashboard.processPluginConfigurationUpdateResult(result))
        .catch((error) => {
          console.error('Failed to save Live TV overrides:', error);
          Dashboard.hideLoadingMsg();
        });
      return false;
    };

    Dashboard.showLoadingMsg();
    Promise.all([
      getConfig.then((config) => config.LiveTvOverrides),
      Xtream.fetchJson('Plugins/JellyfinXtream/v1/LiveTv'),
    ]).then(([data, channels]) => {
      overrideData = data || {};
      for (const channel of channels) {
        overrideData[channel.Id] ??= {};
        const row = createChannelRow(channel, overrideData[channel.Id]);
        table.appendChild(row);
      }
    }).catch((error) => {
      console.error('Failed to load Live TV overrides:', error);
      table.replaceChildren();
      const errorRow = document.createElement('tr');
      const errorCell = document.createElement('td');
      errorCell.colSpan = 3;
      errorCell.textContent = 'Failed to load channels. Existing overrides were not changed.';
      errorRow.appendChild(errorCell);
      table.appendChild(errorRow);
    }).finally(() => Dashboard.hideLoadingMsg());
  }));
}
