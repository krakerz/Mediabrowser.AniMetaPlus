
function aniMetaPlusToast(text) {
    // Jellyfin offers plugin pages no toast of their own; Dashboard.alert is a modal dialog.
    var note = document.createElement('div');
    note.textContent = text;
    note.style.cssText = 'position:fixed;left:50%;bottom:2em;transform:translateX(-50%);background:#303030;color:#fff;padding:.7em 1.2em;border-radius:.3em;z-index:9999;box-shadow:0 2px 8px rgba(0,0,0,.4);';
    document.body.appendChild(note);
    setTimeout(function () { note.remove(); }, 3500);
}

export default aniMetaPlusPage({
    showLoading: function () { Dashboard.showLoadingMsg(); },
    hideLoading: function () { Dashboard.hideLoadingMsg(); },
    toast: aniMetaPlusToast,
    configSaved: function (result) { Dashboard.processPluginConfigurationUpdateResult(result); }
});
