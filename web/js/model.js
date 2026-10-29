/* Model: asset data, persistence, filtering and business rules. No DOM access.
   Category list comes from MasterModel. Data is still stored in the browser (not yet the API). */
const AssetModel = (() => {
  const STORAGE_KEY = 'it-assets';
  const PAGE_SIZE = 10;
  const STATUS = { AVAILABLE: 'Available', IN_USE: 'In Use', MAINTENANCE: 'Maintenance', RETIRED: 'Retired' };
  const STATUS_LABEL = { 'Available': 'พร้อมใช้', 'In Use': 'กำลังใช้', 'Maintenance': 'ซ่อม', 'Retired': 'เลิกใช้' };
  const RETIRE_REASONS = [
    { value: 'Lost', label: 'สูญหาย' },
    { value: 'Stolen', label: 'ถูกขโมย' },
    { value: 'Disposed', label: 'จำหน่าย' },
    { value: 'EndOfLife', label: 'หมดอายุการใช้งาน' },
    { value: 'Other', label: 'อื่น ๆ' },
  ];
  const CONDITIONS = ['ปกติ', 'มีรอยขีดข่วน', 'ชำรุด'];

  const SEED = [
    ['IT-001', 'MacBook Pro 14', 'Notebook', '2025-01-15'],
    ['IT-002', 'Dell Latitude 5440', 'Notebook', '2025-02-10'],
    ['IT-003', 'Lenovo ThinkPad T14', 'Notebook', '2024-11-03'],
    ['IT-004', 'LG UltraWide 34"', 'Monitor', '2024-09-21'],
    ['IT-005', 'Dell U2723QE', 'Monitor', '2025-03-05'],
    ['IT-006', 'Logitech MX Master 3', 'Mouse', '2025-01-20'],
    ['IT-007', 'Logitech MX Keys', 'Keyboard', '2025-01-20'],
    ['IT-008', 'HP LaserJet Pro', 'Printer', '2023-06-12'],
  ].map(([id, name, category, purchaseDate]) => ({
    id, name, category, purchaseDate, assignedTo: '', dueDate: '', status: STATUS.AVAILABLE, history: [],
  }));
  SEED[1].status = STATUS.IN_USE; SEED[1].assignedTo = 'สมชาย ใจดี';
  SEED[7].status = STATUS.MAINTENANCE;

  let assets = Store.get(STORAGE_KEY, SEED);
  // Older saved data has no history / due date / retired fields
  assets.forEach((a) => {
    a.history = a.history || [];
    a.dueDate = a.dueDate || '';
    if (a.status === STATUS.MAINTENANCE && !a.maint) a.maint = { issue: '-', provider: '', openedAt: '' };
  });

  const query = { search: '', category: '', status: '', page: 1 };
  const listeners = [];

  function notify() { listeners.forEach((fn) => fn()); }
  function commit() { Store.set(STORAGE_KEY, assets); notify(); }

  const find = (id) => assets.find((a) => a.id === id);
  const today = () => new Date().toLocaleDateString('en-CA');   // YYYY-MM-DD in local time
  const log = (a, type, text) => a.history.push({ ts: new Date().toISOString(), type, text });

  function filtered() {
    const q = query.search.trim().toLowerCase();
    return assets.filter((a) =>
      (!q || a.id.toLowerCase().includes(q) || a.name.toLowerCase().includes(q)) &&
      (!query.category || a.category === query.category) &&
      (!query.status || a.status === query.status));
  }

  return {
    STATUS, STATUS_LABEL, RETIRE_REASONS, CONDITIONS,
    subscribe: (fn) => listeners.push(fn),
    find,
    getQuery: () => ({ ...query }),
    hasFilters: () => !!(query.search.trim() || query.category || query.status),

    getStats() {
      const count = (s) => assets.filter((a) => a.status === s).length;
      return {
        total: assets.length,
        available: count(STATUS.AVAILABLE),
        inUse: count(STATUS.IN_USE),
        maintenance: count(STATUS.MAINTENANCE),
        retired: count(STATUS.RETIRED),
      };
    },

    // Retired assets are left out of the category breakdown
    getCategoryShares() {
      const live = assets.filter((a) => a.status !== STATUS.RETIRED);
      return MasterModel.getCategories()
        .map((category) => ({ category, count: live.filter((a) => a.category === category).length }))
        .filter((g) => g.count)
        .map((g) => ({ ...g, percent: (g.count / live.length) * 100 }));
    },

    categoryUsage: (category) => assets.filter((a) => a.category === category && a.status !== STATUS.RETIRED).length,

    // Keep assets and the active filter in sync when a category is renamed in master data
    renameCategory(oldName, newName) {
      assets.forEach((a) => { if (a.category === oldName) a.category = newName; });
      if (query.category === oldName) query.category = newName;
      commit();
    },

    getRecent(limit = 6) {
      return assets
        .flatMap((a) => a.history.map((h) => ({ ...h, assetId: a.id, assetName: a.name })))
        .sort((x, y) => y.ts.localeCompare(x.ts))
        .slice(0, limit);
    },

    getAttention() {
      return assets.flatMap((a) => {
        if (a.status === STATUS.MAINTENANCE) return [{ asset: a, text: 'อยู่ระหว่างซ่อม' }];
        if (a.status === STATUS.IN_USE && a.dueDate && a.dueDate < today())
          return [{ asset: a, text: `เกินกำหนดคืน (${a.dueDate}) — ${a.assignedTo}` }];
        return [];
      });
    },

    getPage() {
      const rows = filtered();
      const pages = Math.max(1, Math.ceil(rows.length / PAGE_SIZE));
      query.page = Math.min(query.page, pages);
      return {
        rows: rows.slice((query.page - 1) * PAGE_SIZE, query.page * PAGE_SIZE),
        page: query.page, pages, total: rows.length, pageSize: PAGE_SIZE,
      };
    },

    setSearch(v) { query.search = v; query.page = 1; notify(); },
    setCategory(v) { query.category = v; query.page = 1; notify(); },
    setStatus(v) { query.status = v; query.page = 1; notify(); },
    setPage(n) { query.page = n; notify(); },
    clearFilters() { query.search = ''; query.category = ''; query.status = ''; query.page = 1; notify(); },

    add({ id, name, category, purchaseDate }) {
      id = id.trim().toUpperCase(); name = name.trim();
      if (!id || !name) return { ok: false, error: 'กรุณากรอกรหัสและชื่ออุปกรณ์' };
      if (assets.some((a) => a.id.toUpperCase() === id)) return { ok: false, field: 'id', error: `รหัส ${id} มีอยู่ในระบบแล้ว` };
      const asset = { id, name, category, purchaseDate, assignedTo: '', dueDate: '', status: STATUS.AVAILABLE, history: [] };
      log(asset, 'created', 'เพิ่มอุปกรณ์เข้าระบบ');
      assets.push(asset);
      commit();
      return { ok: true, id };
    },

    update(id, { name, category }) {
      const a = find(id);
      name = name.trim();
      if (!name) return { ok: false, error: 'กรุณากรอกชื่ออุปกรณ์' };
      if (a.status === STATUS.RETIRED) return { ok: false, error: 'อุปกรณ์ที่เลิกใช้งานแล้วแก้ไขไม่ได้' };
      Object.assign(a, { name, category });
      log(a, 'update', 'แก้ไขข้อมูลอุปกรณ์');
      commit();
      return { ok: true };
    },

    // Assets are never deleted: retiring keeps the record and its history searchable
    retire(id, reason) {
      const a = find(id);
      const r = RETIRE_REASONS.find((x) => x.value === reason);
      if (!r) return { ok: false, field: 'reason', error: 'กรุณาเลือกเหตุผล' };
      if (a.status !== STATUS.AVAILABLE) return { ok: false, error: 'เลิกใช้งานได้เฉพาะอุปกรณ์ที่พร้อมใช้ (ถ้ากำลังถูกใช้ให้รับคืนก่อน)' };
      a.status = STATUS.RETIRED; a.retireReason = r.label;
      log(a, 'retired', `เลิกใช้งาน (${r.label})`);
      commit();
      return { ok: true };
    },

    checkOut(id, { employee, dueDate, condition }) {
      const a = find(id);
      if (!employee) return { ok: false, field: 'employee', error: 'กรุณาเลือกพนักงานผู้รับอุปกรณ์' };
      if (dueDate && dueDate < today()) return { ok: false, field: 'dueDate', error: 'วันที่กำหนดคืนต้องไม่เป็นวันที่ผ่านมาแล้ว' };
      if (!a || a.status !== STATUS.AVAILABLE) return { ok: false, error: 'อุปกรณ์ไม่พร้อมให้จ่าย' };
      a.status = STATUS.IN_USE; a.assignedTo = employee; a.dueDate = dueDate || '';
      const extra = [dueDate && `กำหนดคืน ${dueDate}`, condition && `สภาพ: ${condition}`].filter(Boolean).join(' · ');
      log(a, 'check_out', `จ่ายให้ ${employee}${extra ? ' · ' + extra : ''}`);
      commit();
      return { ok: true };
    },

    checkIn(id, { condition, needsMaintenance, notes }) {
      const a = find(id);
      if (!a || a.status !== STATUS.IN_USE) return { ok: false, error: 'อุปกรณ์นี้ไม่ได้ถูกใช้งานอยู่ จึงรับคืนไม่ได้' };
      if (needsMaintenance && !(notes || '').trim()) return { ok: false, field: 'notes', error: 'กรุณาระบุอาการที่ต้องส่งซ่อม' };
      const holder = a.assignedTo;
      a.status = needsMaintenance ? STATUS.MAINTENANCE : STATUS.AVAILABLE;
      a.assignedTo = ''; a.dueDate = '';
      if (needsMaintenance) a.maint = { issue: notes.trim(), provider: '', openedAt: new Date().toISOString() };
      const extra = [condition && `สภาพ: ${condition}`, needsMaintenance && `ส่งซ่อม: ${notes.trim()}`].filter(Boolean).join(' · ');
      log(a, 'check_in', `รับคืนจาก ${holder}${extra ? ' · ' + extra : ''}`);
      commit();
      return { ok: true };
    },

    // Available -> Maintenance (an asset that is in use must be checked in first)
    openMaintenance(id, { issue, provider }) {
      const a = find(id);
      if (!(issue || '').trim()) return { ok: false, field: 'issue', error: 'กรุณาระบุอาการ/ปัญหา' };
      if (!a || a.status !== STATUS.AVAILABLE) return { ok: false, error: 'ส่งซ่อมได้เฉพาะอุปกรณ์ที่พร้อมใช้ (ถ้ากำลังถูกใช้ให้รับคืนก่อน)' };
      a.status = STATUS.MAINTENANCE;
      a.maint = { issue: issue.trim(), provider: (provider || '').trim(), openedAt: new Date().toISOString() };
      log(a, 'maintenance', `ส่งซ่อม: ${a.maint.issue}`);
      commit();
      return { ok: true };
    },

    // Maintenance -> Available, or Retired when it cannot be repaired
    closeMaintenance(id, { resolution, cost, retire, reason }) {
      const a = find(id);
      if (!a || a.status !== STATUS.MAINTENANCE) return { ok: false, error: 'อุปกรณ์นี้ไม่ได้อยู่ระหว่างซ่อม' };
      const amount = cost === '' || cost == null ? null : Number(cost);
      if (amount !== null && (!Number.isFinite(amount) || amount < 0)) return { ok: false, field: 'cost', error: 'ค่าใช้จ่ายต้องเป็นตัวเลขที่ไม่ติดลบ' };
      const r = RETIRE_REASONS.find((x) => x.value === reason);
      if (retire && !r) return { ok: false, field: 'reason', error: 'กรุณาเลือกเหตุผลที่เลิกใช้งาน' };

      const costText = amount === null ? '' : ` · ค่าใช้จ่าย ${amount.toLocaleString('th-TH', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} บาท`;
      const result = (resolution || '').trim();
      a.maint = null;
      if (retire) {
        a.status = STATUS.RETIRED; a.retireReason = r.label;
        log(a, 'retired', `ปิดงานซ่อม — เลิกใช้งาน (${r.label})${result ? ': ' + result : ''}${costText}`);
      } else {
        a.status = STATUS.AVAILABLE;
        log(a, 'maintenance', `ซ่อมเสร็จ${result ? ': ' + result : ''}${costText}`);
      }
      commit();
      return { ok: true };
    },

    getMaintenanceList: () => assets.filter((a) => a.status === STATUS.MAINTENANCE),

    getMovements(limit = 50) {
      return assets
        .flatMap((a) => a.history.filter((h) => h.type === 'check_out' || h.type === 'check_in')
          .map((h) => ({ ...h, assetId: a.id, assetName: a.name })))
        .sort((x, y) => y.ts.localeCompare(x.ts))
        .slice(0, limit);
    },

    getHolderCount: () => new Set(assets.filter((a) => a.status === STATUS.IN_USE).map((a) => a.assignedTo)).size,
    getOverdueCount: () => assets.filter((a) => a.status === STATUS.IN_USE && a.dueDate && a.dueDate < today()).length,

    // Events per calendar month for the last `months` months (oldest first)
    getMonthlyActivity(months = 6) {
      const now = new Date();
      const buckets = Array.from({ length: months }, (_, i) => {
        const d = new Date(now.getFullYear(), now.getMonth() - (months - 1 - i), 1);
        return { key: `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`, label: d.toLocaleDateString('th-TH', { month: 'short' }), count: 0 };
      });
      assets.forEach((a) => a.history.forEach((h) => {
        const b = buckets.find((x) => h.ts.startsWith(x.key));   // ts is ISO (UTC); close enough for a monthly trend
        if (b) b.count += 1;
      }));
      return buckets;
    },

    // CSV for spreadsheets. Cells starting with = + - @ get a leading quote so Excel does not run them as formulas.
    exportCsv() {
      const cell = (v) => {
        let s = String(v ?? '');
        if (/^[=+\-@\t\r]/.test(s)) s = "'" + s;
        return `"${s.replace(/"/g, '""')}"`;
      };
      const head = ['Asset ID', 'ชื่ออุปกรณ์', 'หมวดหมู่', 'สถานะ', 'ผู้ถือครอง', 'กำหนดคืน', 'วันที่ซื้อ', 'เหตุผลเลิกใช้'];
      const rows = assets.map((a) => [a.id, a.name, a.category, STATUS_LABEL[a.status], a.assignedTo, a.dueDate, a.purchaseDate, a.retireReason]);
      return [head, ...rows].map((r) => r.map(cell).join(',')).join('\r\n');
    },
  };
})();
