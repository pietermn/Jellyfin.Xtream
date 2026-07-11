export default function (view) {
  view.addEventListener("viewshow", () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(0);

    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
      view.querySelector('#BaseUrl').value = config.BaseUrl;
      view.querySelector('#Username').value = config.Username;
      view.querySelector('#Password').value = config.Password;
      view.querySelector('#UserAgent').value = config.UserAgent;
      view.querySelector('#PublicServerUrl').value = config.PublicServerUrl || '';
      view.querySelector('#NameCleanupRules').value = config.NameCleanupRules || '';
      Dashboard.hideLoadingMsg();
    });

    const reloadStatus = () => {
      const status = view.querySelector("#ProviderStatus");
      const expiry = view.querySelector("#ProviderExpiry");
      const cons = view.querySelector("#ProviderConnections");
      const maxCons = view.querySelector("#ProviderMaxConnections");
      const time = view.querySelector("#ProviderTime");
      const timezone = view.querySelector("#ProviderTimezone");
      const mpegTs = view.querySelector("#ProviderMpegTs");

      Xtream.fetchJson('Plugins/JellyfinXtream/v1/TestProvider').then(response => {
        status.innerText = response.Status;
        expiry.innerText = response.ExpiryDate;
        cons.innerText = response.ActiveConnections;
        maxCons.innerText = response.MaxConnections;
        time.innerText = response.ServerTime;
        timezone.innerText = response.ServerTimezone;
        mpegTs.innerText = response.SupportsMpegTs;
      }).catch((_) => {
        status.innerText = "Failed. Check server logs.";
        expiry.innerText = "";
        cons.innerText = "";
        maxCons.innerText = "";
        time.innerText = "";
        timezone.innerText = "";
        mpegTs.innerText = "";
      });
    };
    reloadStatus();

    view.querySelector('#UserAgentFromBrowser').onclick = (e) => {
      e.preventDefault();
      view.querySelector('#UserAgent').value = navigator.userAgent;
    };

    const proxyKeyStatus = view.querySelector('#ProxyKeyStatus');
    const rotateProxyKey = (path, confirmation, success) => {
      if (!window.confirm(confirmation)) {
        return;
      }

      Dashboard.showLoadingMsg();
      ApiClient.fetch({
        type: 'POST',
        url: ApiClient.getUrl(`Plugins/JellyfinXtream/v1/ProxyKeys/${path}`),
      }).then(() => {
        proxyKeyStatus.textContent = success;
      }).catch((error) => {
        console.error('Failed to rotate Jellyfin.Xtream proxy key:', error);
        proxyKeyStatus.textContent = 'Revocation failed. Check the server log.';
      }).finally(() => Dashboard.hideLoadingMsg());
    };
    view.querySelector('#RotatePlaybackKey').onclick = () => rotateProxyKey(
      'Playback/Rotate',
      'Revoke all currently issued playback links? Active playback may stop.',
      'All previously issued playback links have been revoked.'
    );
    view.querySelector('#RotateStrmKey').onclick = () => rotateProxyKey(
      'PersistentStrm/Rotate',
      'Revoke all exported STRM links and queue enabled exports for regeneration?',
      'All previous STRM grants were revoked. Enabled exports were queued for regeneration.'
    );

    view.querySelector('#XtreamCredentialsForm').onsubmit = (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId)
        .then((config) => {
          config.BaseUrl = view.querySelector('#BaseUrl').value;
          config.Username = view.querySelector('#Username').value;
          config.Password = view.querySelector('#Password').value;
          config.UserAgent = view.querySelector('#UserAgent').value;
          config.PublicServerUrl = view.querySelector('#PublicServerUrl').value;
          config.NameCleanupRules = view.querySelector('#NameCleanupRules').value;
          return ApiClient.updatePluginConfiguration(pluginId, config);
        })
        .then((result) => {
          reloadStatus();
          Dashboard.processPluginConfigurationUpdateResult(result);
        })
        .catch((error) => {
          console.error('Failed to save Jellyfin.Xtream credentials:', error);
          Dashboard.hideLoadingMsg();
        });

      return false;
    };
  }));
}
