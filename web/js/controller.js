/* Controller: connects user actions from the View to the Models, enforces permissions,
   and refreshes the View whenever a model changes. */
const AssetController = (() => {
  const M = AssetModel;
  const D = MasterModel;
  const A = AuthModel;
  const V = AssetView;

  const VIEWS = ['overview', 'assets', 'movements', 'maintenance', 'master', 'users'];
  // The address hash (#assets) lets a page be bookmarked and reloaded in place
  const hashView = () => (VIEWS.includes(location.hash.slice(1)) ? location.hash.slice(1) : 'overview');
  let currentView = hashView();
  // Which asset the open dialog is acting on
  let editingId = null;
  let attention = [];   // rows currently shown in "สิ่งที่ต้องติดตาม"
  let managedUsers = [];

  // Counts come straight from the data; each row jumps to the page where it can be handled
  function buildAttention() {
    const overdue = M.getOverdueCount();
    const repairs = M.getMaintenanceList().length;
    return [
      overdue && { label: 'กำหนดคืนเกินวันที่', count: overdue, tone: 'red', go: { view: 'assets', status: M.STATUS.IN_USE } },
      repairs && { label: 'งานซ่อมที่ยังเปิดอยู่', count: repairs, tone: 'amber', go: { view: 'maintenance' } },
    ].filter(Boolean);
  }

  function render() {
    const me = A.current();
    if (!me) {
      V.showLogin();
      if (A.consumeExpired()) V.setLoginError('เซสชันหมดอายุ กรุณาเข้าสู่ระบบอีกครั้ง');
      return;
    }

    if (currentView === 'users' && !A.can('user.manage')) currentView = 'overview';
    const canManageMaster = A.can('master.manage');
    const categories = D.getCategories();

    V.showApp(me, A.ROLES[me.role].label);
    V.applyPermissions(A.can);
    V.setView(currentView);

    V.renderStats(M.getStats(), { holders: M.getHolderCount() });
    V.renderBars(M.getMonthlyActivity(6));
    V.renderShares(M.getCategoryShares());
    attention = buildAttention();
    V.renderAttention(attention);
    V.renderRecent(M.getRecent());
    V.renderMovements(M.getMovements());
    V.renderMaintenance(M.getMaintenanceList(), A.can);

    V.renderCategoryOptions(categories, M.getQuery().category);
    V.renderTable(M.getPage(), M.STATUS, A.can, M.hasFilters());

    V.renderCategories(categories, M.categoryUsage, canManageMaster);
    V.renderEmployees(D.getEmployees(), canManageMaster);
    V.renderUsers(managedUsers, me.id);
  }

  async function loadUsers() {
    const result = await Api.get('/users');
    if (!result.ok) { V.toast(result.error, true); return result; }
    managedUsers = result.data || [];
    V.renderUsers(managedUsers, A.current()?.id);
    return result;
  }
  // Wrap a handler so it only runs when the signed-in user holds the permission
  const guard = (perm, fn) => (...args) => {
    if (!A.can(perm)) return V.toast('คุณไม่มีสิทธิ์ทำรายการนี้', true);
    return fn(...args);
  };

  // Show a form; the success message may be a string or a function of the submitted values
  const form = (title, fields, submit, successMsg, opts) => V.showForm(title, fields, async (values) => {
    const result = await submit(values);
    if (result.ok) V.toast(typeof successMsg === 'function' ? successMsg(values) : successMsg);
    return result;
  }, opts);

  const asOptions = (items, placeholder) =>
    [...(placeholder ? [{ value: '', label: placeholder }] : []), ...items.map((v) => ({ value: v, label: v }))];

  function openCheckout(id) {
    const asset = M.find(id);
    const employees = D.getEmployees();
    if (!employees.length) return V.toast('ยังไม่มีพนักงาน — เพิ่มได้ที่เมนู "ข้อมูลหลัก" ก่อน', true);
    form(`จ่ายอุปกรณ์ ${asset.id}`, [
      { type: 'info', text: `${asset.id} — ${asset.name}` },
      {
        name: 'employee', label: 'ผู้รับอุปกรณ์', required: true,
        options: [{ value: '', label: '— เลือกพนักงาน —' },
          ...employees.map((e) => ({ value: e.name, label: e.department ? `${e.name} (${e.department})` : e.name }))],
      },
      { name: 'dueDate', label: 'วันที่กำหนดคืน', type: 'date', hint: 'ไม่บังคับ' },
      { name: 'condition', label: 'สภาพก่อนจ่าย', value: M.CONDITIONS[0], options: asOptions(M.CONDITIONS) },
    ], (v) => M.checkOut(id, v), (v) => `จ่ายอุปกรณ์ ${id} ให้ ${v.employee} สำเร็จ`, { submitLabel: 'ยืนยันการจ่าย' });
  }

  function openCheckin(id) {
    const asset = M.find(id);
    form(`รับคืนอุปกรณ์ ${asset.id}`, [
      { type: 'info', text: `${asset.id} — ${asset.name} · ผู้ถือครอง: ${asset.assignedTo}` },
      { name: 'condition', label: 'สภาพหลังคืน', value: M.CONDITIONS[0], options: asOptions(M.CONDITIONS) },
      { type: 'checkbox', name: 'needsMaintenance', label: 'ต้องส่งซ่อม' },
      { name: 'notes', label: 'อาการ / หมายเหตุ', type: 'textarea', required: true, showWhen: 'needsMaintenance' },
    ], (v) => M.checkIn(id, v),
    (v) => (v.needsMaintenance ? `รับคืนอุปกรณ์ ${id} แล้ว — สถานะเปลี่ยนเป็น "ซ่อม"` : `รับคืนอุปกรณ์ ${id} สำเร็จ`),
    { submitLabel: 'ยืนยันการรับคืน' });
  }

  function openRetire(id) {
    const asset = M.find(id);
    form(`เลิกใช้งานอุปกรณ์ ${asset.id}`, [
      { type: 'info', text: `${asset.id} — ${asset.name} จะถูกเปลี่ยนเป็น "เลิกใช้" และกลับมาใช้ไม่ได้ แต่ยังค้นประวัติได้` },
      { name: 'reason', label: 'เหตุผล', required: true, options: [{ value: '', label: '— เลือกเหตุผล —' }, ...M.RETIRE_REASONS] },
    ], (v) => M.retire(id, v.reason), `เลิกใช้งานอุปกรณ์ ${id} เรียบร้อยแล้ว`, { submitLabel: 'เลิกใช้งาน', danger: true });
  }

  function openSendRepair(id) {
    const asset = M.find(id);
    form(`ส่งซ่อมอุปกรณ์ ${asset.id}`, [
      { type: 'info', text: `${asset.id} — ${asset.name}` },
      { name: 'issue', label: 'อาการ / ปัญหา', type: 'textarea', required: true },
      { name: 'provider', label: 'ผู้ให้บริการซ่อม', hint: 'ไม่บังคับ' },
    ], (v) => M.openMaintenance(id, v), `ส่งซ่อมอุปกรณ์ ${id} แล้ว`, { submitLabel: 'ยืนยันส่งซ่อม' });
  }

  function openCloseRepair(id) {
    const asset = M.find(id);
    form(`ปิดงานซ่อม ${asset.id}`, [
      { type: 'info', text: `${asset.id} — ${asset.name} · อาการ: ${asset.maint?.issue || '-'}` },
      { name: 'resolution', label: 'ผลการซ่อม', type: 'textarea' },
      { name: 'cost', label: 'ค่าใช้จ่าย (บาท)', type: 'number', hint: 'ไม่บังคับ' },
      { type: 'checkbox', name: 'retire', label: 'ซ่อมไม่ได้ — เลิกใช้งานอุปกรณ์นี้' },
      { name: 'reason', label: 'เหตุผลที่เลิกใช้งาน', required: true, showWhen: 'retire', options: [{ value: '', label: '— เลือกเหตุผล —' }, ...M.RETIRE_REASONS] },
    ], (v) => M.closeMaintenance(id, v),
    (v) => (v.retire ? `ปิดงานซ่อม ${id} — เลิกใช้งานแล้ว` : `ปิดงานซ่อม ${id} — พร้อมใช้งานอีกครั้ง`),
    { submitLabel: 'ปิดงานซ่อม' });
  }

  const handlers = {
    /* ---------- Session ---------- */
    async onLogin(username, password) {
      const result = await A.login(username, password);
      if (result.ok) { V.resetLogin(); currentView = 'overview'; } else V.setLoginError(result.error);
    },
    async onLogout() { currentView = 'overview'; await A.logout(); },
    onNavigate(view) {
      if (view === 'users' && !A.can('user.manage')) return V.toast('คุณไม่มีสิทธิ์เข้าถึงหน้านี้', true);
      currentView = view; history.replaceState(null, '', '#' + view); render();
      if (view === 'users') loadUsers();
    },
    onChangePassword() {
      form('เปลี่ยนรหัสผ่าน', [
        { name: 'old', label: 'รหัสผ่านเดิม', type: 'password', required: true },
        { name: 'next', label: 'รหัสผ่านใหม่', type: 'password', required: true, hint: 'อย่างน้อย 10 ตัวอักษร มีทั้งตัวอักษรและตัวเลข' },
        { name: 'confirm', label: 'ยืนยันรหัสผ่านใหม่', type: 'password', required: true },
      ], async (v) => {
        if (v.next !== v.confirm) return { ok: false, field: 'confirm', error: 'รหัสผ่านใหม่ไม่ตรงกัน' };
        return A.changeOwnPassword(v.old, v.next);
      }, 'เปลี่ยนรหัสผ่านเรียบร้อยแล้ว');
    },

    /* ---------- Assets ---------- */
    onAddClick: guard('asset.create', () => {
      editingId = null;
      V.showAssetForm(null);
    }),

    onAssetSubmit() {
      if (!A.can(editingId ? 'asset.edit' : 'asset.create')) return V.toast('คุณไม่มีสิทธิ์ทำรายการนี้', true);
      const values = V.getAssetForm();
      const result = editingId ? M.update(editingId, values) : M.add(values);
      if (!result.ok) return V.setAssetError(result.error, result.field);
      V.toast(editingId ? `แก้ไขอุปกรณ์ ${editingId} เรียบร้อยแล้ว` : 'บันทึกอุปกรณ์ใหม่เรียบร้อยแล้ว!');
      V.closeModals();
    },

    onRowAction(action, id) {
      switch (action) {
        case 'detail': V.showDetail(M.find(id), A.can, M.STATUS); break;
        case 'checkout': guard('asset.checkout', () => openCheckout(id))(); break;
        case 'checkin': guard('asset.checkin', () => openCheckin(id))(); break;
        case 'edit': guard('asset.edit', () => { editingId = id; V.showAssetForm(M.find(id)); })(); break;
        case 'retire': guard('asset.retire', () => openRetire(id))(); break;
        case 'send-repair': guard('maintenance.manage', () => openSendRepair(id))(); break;
        case 'close-repair': guard('maintenance.manage', () => openCloseRepair(id))(); break;
        case 'attention': {
          const go = attention[Number(id)]?.go;
          if (!go) break;
          if (go.status) M.setStatus(go.status); else M.clearFilters();
          V.syncStatusTab(go.status || '');
          handlers.onNavigate(go.view);
          break;
        }
      }
    },

    onSearch: (v) => M.setSearch(v),
    onCategoryFilter: (v) => M.setCategory(v),
    onStatusFilter: (v) => M.setStatus(v),
    onPageChange: (n) => M.setPage(n),
    onExport() {
      V.download(`assets-${new Date().toLocaleDateString('en-CA')}.csv`, M.exportCsv());
      V.toast('ส่งออกรายการอุปกรณ์เป็นไฟล์ CSV แล้ว');
    },
    onClearFilters() { V.resetFilterControls(); M.clearFilters(); },

    onUserCreate: guard('user.manage', () => {
      const roles = [{ value: 'Auditor', label: 'ผู้ตรวจสอบ (อ่านข้อมูล)' }, { value: 'ITStaff', label: 'เจ้าหน้าที่ IT' }, { value: 'Admin', label: 'ผู้ดูแลระบบ' }];
      form('เพิ่มบัญชีผู้ใช้', [
        { name: 'username', label: 'ชื่อผู้ใช้', required: true, hint: 'a-z, 0-9, จุด ขีดล่าง หรือขีดกลาง (3–32 ตัวอักษร)' },
        { name: 'displayName', label: 'ชื่อที่แสดง', required: true },
        { name: 'role', label: 'บทบาท', required: true, options: roles },
        { name: 'password', label: 'รหัสผ่านเริ่มต้น', type: 'password', required: true, hint: 'อย่างน้อย 10 ตัวอักษร มีทั้งตัวอักษรและตัวเลข' },
      ], async (v) => {
        const result = await Api.post('/users', { username: v.username, displayName: v.displayName, role: v.role, password: v.password });
        if (result.ok) await loadUsers();
        return result;
      }, 'เพิ่มบัญชีผู้ใช้เรียบร้อยแล้ว');
    }),
    onRefreshUsers: () => loadUsers(),
    onUserAction: guard('user.manage', (action, id) => {
      const user = managedUsers.find((u) => u.id === id);
      if (!user) return V.toast('ไม่พบบัญชีผู้ใช้ กรุณารีเฟรชรายการ', true);
      const own = A.current()?.id === id;
      const roles = [{ value: 'Auditor', label: 'ผู้ตรวจสอบ (อ่านข้อมูล)' }, { value: 'ITStaff', label: 'เจ้าหน้าที่ IT' }, { value: 'Admin', label: 'ผู้ดูแลระบบ' }];
      if (action === 'edit') {
        const fields = [{ name: 'displayName', label: 'ชื่อที่แสดง', value: user.displayName, required: true }];
        if (!own) fields.push(
          { name: 'role', label: 'บทบาท', value: user.role, options: roles },
          { name: 'active', label: 'สถานะบัญชี', value: String(user.active), options: [{ value: 'true', label: 'เปิดใช้งาน' }, { value: 'false', label: 'ปิดบัญชี' }] },
          { name: 'confirmPassword', label: 'ยืนยันรหัสผ่านผู้ดูแล', type: 'password', required: true, hint: 'จำเป็นเมื่อเปลี่ยน Role หรือปิดบัญชี' },
        );
        return form('แก้ไขบัญชีผู้ใช้', fields, async (v) => {
          const body = { displayName: v.displayName };
          if (!own) Object.assign(body, { role: v.role, active: v.active === 'true', confirmPassword: v.confirmPassword });
          const result = await Api.patch(`/users/${encodeURIComponent(id)}`, body);
          if (result.ok) await loadUsers();
          return result;
        }, 'บันทึกข้อมูลผู้ใช้แล้ว');
      }
      if (action === 'toggle') {
        const active = !user.active;
        return form(active ? 'เปิดบัญชีผู้ใช้' : 'ปิดบัญชีผู้ใช้', [
          { type: 'info', text: `${user.username} · ${user.displayName}` },
          { name: 'confirmPassword', label: 'รหัสผ่านผู้ดูแล', type: 'password', required: true },
        ], async (v) => {
          const result = await Api.patch(`/users/${encodeURIComponent(id)}`, { active, confirmPassword: v.confirmPassword });
          if (result.ok) await loadUsers();
          return result;
        }, active ? 'เปิดบัญชีผู้ใช้แล้ว' : 'ปิดบัญชีผู้ใช้แล้ว', { submitLabel: active ? 'เปิดบัญชี' : 'ปิดบัญชี', danger: !active });
      }
      if (action === 'reset') {
        return form('รีเซ็ตรหัสผ่าน', [
          { type: 'info', text: `${user.username} · ${user.displayName}` },
          { name: 'newPassword', label: 'รหัสผ่านใหม่', type: 'password', required: true, hint: 'อย่างน้อย 10 ตัวอักษร มีทั้งตัวอักษรและตัวเลข' },
          { name: 'confirmPassword', label: 'รหัสผ่านผู้ดูแลเพื่อยืนยัน', type: 'password', required: true },
        ], async (v) => {
          const result = await Api.post(`/users/${encodeURIComponent(id)}/reset-password`, { newPassword: v.newPassword, confirmPassword: v.confirmPassword });
          if (result.ok) await loadUsers();
          return result;
        }, 'รีเซ็ตรหัสผ่านเรียบร้อยแล้ว');
      }
    }),
    /* ---------- Master data: categories ---------- */
    onCategoryAction: guard('master.manage', (action, name) => {
      const nameField = (value = '') => [{ name: 'name', label: 'ชื่อหมวดหมู่', value, required: true }];
      if (action === 'add') {
        form('เพิ่มหมวดหมู่', nameField(), (v) => D.addCategory(v.name), 'เพิ่มหมวดหมู่เรียบร้อยแล้ว');
      } else if (action === 'rename') {
        form('แก้ไขหมวดหมู่', nameField(name), (v) => {
          const result = D.renameCategory(name, v.name);
          if (result.ok) M.renameCategory(name, result.name);
          return result;
        }, 'แก้ไขหมวดหมู่เรียบร้อยแล้ว');
      } else if (action === 'delete') {
        const usage = M.categoryUsage(name);
        if (usage) return V.toast(`ลบไม่ได้: มีอุปกรณ์ ${usage} รายการใช้หมวดหมู่ ${name}`, true);
        V.showConfirm(`ลบหมวดหมู่ ${name}?`, () => {
          const result = D.removeCategory(name);
          V.toast(result.ok ? `ลบหมวดหมู่ ${name} เรียบร้อยแล้ว` : result.error, !result.ok);
        }, { title: 'ลบหมวดหมู่', label: 'ลบ' });
      }
    }),

    /* ---------- Master data: employees ---------- */
    onEmployeeAction: guard('master.manage', (action, id) => {
      const fields = (e = {}) => [
        { name: 'name', label: 'ชื่อ-สกุล', value: e.name, required: true },
        { name: 'department', label: 'แผนก', value: e.department },
      ];
      if (action === 'add') {
        form('เพิ่มพนักงาน', fields(), (v) => D.addEmployee(v), 'เพิ่มพนักงานเรียบร้อยแล้ว');
      } else if (action === 'edit') {
        form('แก้ไขพนักงาน', fields(D.findEmployee(id)), (v) => D.updateEmployee(id, v), 'แก้ไขพนักงานเรียบร้อยแล้ว');
      } else if (action === 'delete') {
        V.showConfirm(`ลบพนักงาน ${D.findEmployee(id).name}?`, () => {
          D.removeEmployee(id);
          V.toast('ลบพนักงานเรียบร้อยแล้ว');
        }, { title: 'ลบพนักงาน', label: 'ลบ' });
      }
    }),
  };

  return {
    async init() {
      V.bind(handlers);
      [M, D, A].forEach((model) => model.subscribe(render));
      await A.restore();   // resumes the session if the cookie is still valid; renders either way
      if (currentView === 'users' && A.can('user.manage')) await loadUsers();
      render();
    },
  };
})();

AssetController.init();






