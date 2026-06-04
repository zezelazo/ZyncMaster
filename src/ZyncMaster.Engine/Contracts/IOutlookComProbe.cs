namespace ZyncMaster.Engine;

// Probes whether Outlook Classic COM automation is available on this device, so the UI can
// gate COM-only features (the OutlookCom source tile, the local .txt export) instead of
// failing later when CalExport.exe cannot reach Outlook.
public interface IOutlookComProbe
{
    // True when Outlook Classic appears installed (its Outlook.Application ProgID is registered).
    // Never throws and never launches Outlook; returns false off Windows or on any error.
    bool IsAvailable();
}
