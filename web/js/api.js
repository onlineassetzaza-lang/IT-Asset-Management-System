/* Thin client for the REST API. Returns { ok, data } or { ok: false, status, error }. */
const Api = (() => {
  const BASE = '/api/v1';
  const api = { onUnauthorized: null };

  function errorMessage(data) {
    if (data && data.errors) return Object.values(data.errors).flat()[0];
    return (data && (data.detail || data.title)) || 'เกิดข้อผิดพลาด กรุณาลองใหม่อีกครั้ง';
  }

  async function request(method, path, body) {
    let res;
    try {
      res = await fetch(BASE + path, {
        method,
        credentials: 'same-origin',
        // Custom header required by the API for state-changing calls (CSRF defence)
        headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'itam' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch (e) {
      return { ok: false, status: 0, error: 'เชื่อมต่อเซิร์ฟเวอร์ไม่ได้ — เปิดระบบผ่านที่อยู่ของ API (เช่น http://localhost:5080) ไม่ใช่เปิดไฟล์โดยตรง' };
    }
    if (res.status === 401 && path !== '/auth/login' && api.onUnauthorized) api.onUnauthorized();
    if (res.status === 204) return { ok: true, data: null };
    let data = null;
    try { data = await res.json(); } catch (e) { /* empty or non-JSON body */ }
    return res.ok ? { ok: true, data } : { ok: false, status: res.status, error: errorMessage(data) };
  }

  return Object.assign(api, {
    get: (path) => request('GET', path),
    post: (path, body) => request('POST', path, body ?? {}),
    patch: (path, body) => request('PATCH', path, body),
  });
})();
