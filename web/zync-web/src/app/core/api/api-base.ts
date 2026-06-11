// Same-origin path prefix of the SyncMaster API. In production nginx proxies
// https://api.devlabperu.com/zync/ -> Kestrel; in dev the Angular proxy maps it
// to the local server (proxy.conf.json). Single constant, no environments file.
export const API_BASE = '/zync';
