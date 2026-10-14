    return aniMetaPlusPage({
        showLoading: function () { loading.show(); },
        hideLoading: function () { loading.hide(); },
        toast: function (text) { toast(text); },
        configSaved: function (result) { Dashboard.processPluginConfigurationUpdateResult(result); }
    });
});
