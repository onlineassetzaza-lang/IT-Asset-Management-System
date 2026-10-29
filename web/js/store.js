/* Thin localStorage/sessionStorage wrapper; storage may be unavailable or hold corrupt data. */
const Store = {
  get(key, fallback, area = localStorage) {
    try {
      const data = JSON.parse(area.getItem(key));
      if (data !== null) return data;
    } catch (e) { /* use fallback */ }
    return fallback;
  },
  set(key, value, area = localStorage) {
    try { area.setItem(key, JSON.stringify(value)); } catch (e) { /* storage unavailable */ }
  },
  remove(key, area = localStorage) {
    try { area.removeItem(key); } catch (e) { /* storage unavailable */ }
  },
};
