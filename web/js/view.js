/* View: DOM rendering and event binding. Holds no business data; reports user actions via handlers. */
const AssetView = (() => {
  const STATUS_CLASS = { 'Available': 'available', 'In Use': 'inuse', 'Maintenance': 'maintenance', 'Retired': 'retired' };
  const VIEW_TITLES = {
    overview: 'ภาพรวม', assets: 'อุปกรณ์', movements: 'เบิก–คืนอุปกรณ์', maintenance: 'งานซ่อมบำรุง', master: 'ตั้งค่าระบบ', users: 'ผู้ใช้และสิทธิ์',
  };
  const VIEWS = Object.keys(VIEW_TITLES);

  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString('th-TH', { dateStyle: 'medium', timeStyle: 'short' }) : '-');

  /** Short time for feeds: HH:mm today, "เมื่อวาน", otherwise the date. */
  function fmtRelative(iso) {
    const d = new Date(iso);
    const startOfDay = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const days = Math.round((startOfDay(new Date()) - startOfDay(d)) / 86400000);
    if (days === 0) return d.toLocaleTimeString('th-TH', { hour: '2-digit', minute: '2-digit' });
    if (days === 1) return 'เมื่อวาน';
    return d.toLocaleDateString('th-TH', { day: 'numeric', month: 'short' });
  }

  const overlays = () => document.querySelectorAll('.modal, .drawer');
  const openOverlay = (id) => $(id).classList.remove('hidden');
  const closeModals = () => overlays().forEach((m) => m.classList.add('hidden'));
  const options = (list, selected) => list.map((c) => `<option value="${esc(c)}"${c === selected ? ' selected' : ''}>${esc(c)}</option>`).join('');
  const btn = (attrs, label, title = '', cls = '') =>
    `<button class="btn sm ${cls}" ${attrs} title="${esc(title || label)}">${label}</button>`;

  /** Action buttons allowed for this asset's status and the user's permissions. */
  function assetActions(a, can, statusEnum, ctx) {
    const id = `data-id="${esc(a.id)}"`;
    const out = [];
    if (a.status === statusEnum.AVAILABLE && can('asset.checkout')) out.push(btn(`data-act="checkout" ${id}`, '↗ จ่าย', 'จ่ายอุปกรณ์', ctx === 'drawer' ? 'primary' : ''));
    if (a.status === statusEnum.IN_USE && can('asset.checkin')) out.push(btn(`data-act="checkin" ${id}`, '↙ รับคืน', 'รับคืนอุปกรณ์', ctx === 'drawer' ? 'primary' : ''));
    if (a.status !== statusEnum.RETIRED && can('asset.edit')) out.push(btn(`data-act="edit" ${id}`, '✎ แก้ไข', 'แก้ไขข้อมูล'));
    if (a.status === statusEnum.AVAILABLE && can('maintenance.manage')) out.push(btn(`data-act="send-repair" ${id}`, '⚒ ส่งซ่อม', 'ส่งอุปกรณ์ซ่อม'));
    if (a.status === statusEnum.MAINTENANCE && can('maintenance.manage')) out.push(btn(`data-act="close-repair" ${id}`, '✓ ปิดงานซ่อม', 'ปิดงานซ่อม', ctx === 'drawer' ? 'primary' : ''));
    if (a.status === statusEnum.AVAILABLE && can('asset.retire')) out.push(btn(`data-act="retire" ${id}`, '⊘ เลิกใช้งาน', 'เลิกใช้งานอุปกรณ์'));
    return out;
  }

  let formCallback = null;
  let confirmCallback = null;
  let lastView = null;

  return {
    /* ---------- Session / layout ---------- */
    hideBoot() { $('boot').classList.add('hidden'); },
    showLogin() { this.hideBoot(); $('login').classList.remove('hidden'); $('app').classList.add('hidden'); closeModals(); },
    showApp(user, roleLabel) {
      this.hideBoot();
      $('login').classList.add('hidden'); $('app').classList.remove('hidden');
      $('user-name').textContent = user.displayName;
      $('user-role').textContent = roleLabel;
      $('user-avatar').textContent = (user.displayName || user.username || '?').trim().charAt(0).toUpperCase();
    },
    resetLogin() { $('form-login').reset(); $('login-error').textContent = ''; },
    setLoginError: (msg) => { $('login-error').textContent = msg; },

    applyPermissions(can) {
      document.querySelectorAll('[data-perm]').forEach((el) => el.classList.toggle('hidden', !can(el.dataset.perm)));
    },
    setView(name) {
      VIEWS.forEach((v) => $('view-' + v).classList.toggle('hidden', v !== name));
      document.querySelectorAll('.nav-item[data-view]').forEach((t) => {
        t.classList.toggle('active', t.dataset.view === name);
        t.toggleAttribute('aria-current', t.dataset.view === name);
      });
      $('crumb-page').textContent = VIEW_TITLES[name];
      if (name !== lastView) { window.scrollTo(0, 0); lastView = name; }   // only when navigating, not on every re-render
    },
    setMasterTab(name) {
      document.querySelectorAll('#master-tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.sub === name));
      $('sub-categories').classList.toggle('hidden', name !== 'categories');
      $('sub-employees').classList.toggle('hidden', name !== 'employees');
    },


    /* ---------- User and role management ---------- */
    renderUsers(users, currentUserId) {
      const roleLabel = { Admin: 'ผู้ดูแลระบบ', ITStaff: 'เจ้าหน้าที่ IT', Auditor: 'ผู้ตรวจสอบ' };
      $('users-body').innerHTML = users.map((u) => {
        const locked = u.lockedUntil && new Date(u.lockedUntil) > new Date();
        const status = !u.active ? '<span class="badge retired">ปิดใช้งาน</span>' : locked ? '<span class="badge maintenance">ล็อกชั่วคราว</span>' : '<span class="badge available">ใช้งาน</span>';
        const id = `data-id="${esc(u.id)}"`;
        const own = u.id === currentUserId;
        const actions = [btn(`data-act="edit" ${id}`, '✎ แก้ไข', 'แก้ไขผู้ใช้'),
          !own && btn(`data-act="reset" ${id}`, '↻ รีเซ็ตรหัส', 'รีเซ็ตรหัสผ่าน'),
          !own && btn(`data-act="toggle" ${id}`, u.active ? 'ปิดบัญชี' : 'เปิดบัญชี', u.active ? 'ปิดบัญชี' : 'เปิดบัญชี')].filter(Boolean).join('');
        return `<tr><td><strong>${esc(u.username)}</strong></td><td>${esc(u.displayName)}</td><td>${esc(roleLabel[u.role] || u.role)}</td><td>${status}</td><td class="row-actions">${actions}</td></tr>`;
      }).join('') || '<tr><td class="empty" colspan="5">ยังไม่มีบัญชีผู้ใช้</td></tr>';
    },
    /* ---------- Overview ---------- */
    /** stats + { holders, overdue }: every sub-line is derived from real data. */
    renderStats(stats, extra) {
      const pct = stats.total ? ((stats.available / stats.total) * 100).toFixed(2) : '0.00';
      $('c-total').textContent = stats.total;
      $('s-total').textContent = stats.retired ? `รวมเลิกใช้แล้ว ${stats.retired} รายการ` : 'อุปกรณ์ในทะเบียน';
      $('c-available').textContent = stats.available;
      $('s-available').textContent = `${pct}% ของทั้งหมด`;
      $('c-inuse').textContent = stats.inUse;
      $('s-inuse').textContent = stats.inUse ? `ผู้ถือครอง ${extra.holders} คน` : 'ไม่มีการใช้งาน';
      $('c-maintenance').textContent = stats.maintenance;
      $('s-maintenance').textContent = stats.maintenance ? `อยู่ระหว่างซ่อม ${stats.maintenance} รายการ` : 'ไม่มีงานซ่อมค้าง';
    },

    /** months: [{ label, count }] oldest first */
    renderBars(months) {
      const max = Math.max(...months.map((m) => m.count));
      if (!max) {
        $('bars').outerHTML = '<div id="bars" class="chart-empty">ยังไม่มีการเคลื่อนไหวใน 6 เดือนล่าสุด</div>';
        $('bar-labels')?.remove();
        return;
      }
      const el = $('bars');
      el.className = 'bars';
      el.innerHTML = months.map((m) =>
        `<div class="bar"><span style="height:${Math.max((m.count / max) * 100, 2)}%" title="${m.count} รายการ"></span></div>`).join('');
      let labels = $('bar-labels');
      if (!labels) { labels = document.createElement('div'); labels.id = 'bar-labels'; labels.className = 'bar-labels'; el.after(labels); }
      labels.innerHTML = months.map((m) => `<span>${esc(m.label)}<br><b>${m.count}</b></span>`).join('');
    },

    renderShares(shares) {
      $('shares').innerHTML = shares.map((s) =>
        `<li><span>${esc(s.category)}</span><span class="pct">${s.percent.toFixed(2)}%</span><span class="track"><i style="width:${s.percent}%"></i></span></li>`).join('')
        || '<li class="feed-empty">ยังไม่มีข้อมูลอุปกรณ์</li>';
    },

    /** items: [{ label, count, tone: red|amber|green, go: { view, status? } }] — each row jumps to the relevant page */
    renderAttention(items) {
      $('attention').innerHTML = items.map((i, n) =>
        `<li><button class="row-link" data-act="attention" data-id="${n}"><span>${esc(i.label)}</span><span class="count ${i.tone}">${i.count}</span></button></li>`).join('')
        || '<li class="feed-empty">ไม่มีรายการที่ต้องติดตาม</li>';
    },

    renderRecent(events) {
      $('recent').innerHTML = events.map((e) =>
        `<li><span class="main"><button class="link" data-act="detail" data-id="${esc(e.assetId)}">${esc(e.assetName)}</button><small>${esc(e.text)}</small></span><span class="when">${esc(fmtRelative(e.ts))}</span></li>`).join('')
        || '<li class="feed-empty">ยังไม่มีรายการเคลื่อนไหว</li>';
    },

    renderMovements(events) {
      $('movement-body').innerHTML = events.map((e) => `
        <tr>
          <td>${esc(fmtDate(e.ts))}</td>
          <td><button class="link" data-act="detail" data-id="${esc(e.assetId)}">${esc(e.assetId)}</button></td>
          <td>${esc(e.assetName)}</td>
          <td class="wrap">${esc(e.text)}</td>
        </tr>`).join('') || '<tr><td colspan="4" class="feed-empty">ยังไม่มีรายการจ่ายหรือรับคืน</td></tr>';
    },

    renderMaintenance(assets, can) {
      $('maint-body').innerHTML = assets.map((a) => `
        <tr>
          <td><button class="link" data-act="detail" data-id="${esc(a.id)}">${esc(a.id)}</button></td>
          <td>${esc(a.name)}</td>
          <td class="wrap">${esc(a.maint?.issue || '-')}</td>
          <td>${esc(a.maint?.provider || '-')}</td>
          <td>${esc(fmtDate(a.maint?.openedAt))}</td>
          <td class="row-actions">${can('maintenance.manage') ? btn(`data-act="close-repair" data-id="${esc(a.id)}"`, '✓ ปิดงานซ่อม', 'ปิดงานซ่อม', 'primary') : '-'}</td>
        </tr>`).join('') || '<tr><td colspan="6" class="feed-empty">ไม่มีงานซ่อมที่เปิดอยู่</td></tr>';
    },

    /** Saves text as a file in the browser. A BOM keeps Thai readable when opened in Excel. */
    download(filename, text, type = 'text/csv;charset=utf-8') {
      const url = URL.createObjectURL(new Blob(['﻿', text], { type }));
      const a = Object.assign(document.createElement('a'), { href: url, download: filename });
      document.body.appendChild(a); a.click(); a.remove();
      URL.revokeObjectURL(url);
    },

    /* ---------- Assets view ---------- */
    renderCategoryOptions(categories, selectedFilter) {
      $('filter-category').innerHTML = '<option value="">ทุกหมวดหมู่</option>' + options(categories, selectedFilter);
      const current = $('f-category').value;
      $('f-category').innerHTML = options(categories, current);
    },

    renderTable({ rows, page, pages, total, pageSize }, statusEnum, can, hasFilters) {
      $('tbody').innerHTML = rows.map((a) => `
        <tr>
          <td><button class="link" data-act="detail" data-id="${esc(a.id)}">${esc(a.id)}</button></td>
          <td>${esc(a.name)}</td>
          <td>${esc(a.category)}</td>
          <td>${a.assignedTo ? esc(a.assignedTo) : '-'}</td>
          <td><span class="badge ${STATUS_CLASS[a.status]}">${esc(this.statusLabel(a.status))}</span></td>
          <td class="row-actions">${assetActions(a, can, statusEnum, 'row').join('') || '-'}</td>
        </tr>`).join('');

      const empty = rows.length === 0;
      $('empty-state').classList.toggle('hidden', !empty);
      $('empty-text').textContent = hasFilters ? 'ไม่พบอุปกรณ์ที่ตรงกับตัวกรอง' : 'ยังไม่มีอุปกรณ์ในระบบ';
      $('empty-clear').classList.toggle('hidden', !hasFilters);
      $('result-count').textContent = empty ? '' : `แสดง ${(page - 1) * pageSize + 1}–${(page - 1) * pageSize + rows.length} จาก ${total} รายการ`;

      $('pagination').innerHTML = pages > 1
        ? `<button class="btn sm" data-page="${page - 1}" ${page === 1 ? 'disabled' : ''}>‹ ก่อนหน้า</button>`
          + Array.from({ length: pages }, (_, i) =>
            `<button class="btn sm ${i + 1 === page ? 'active' : ''}" data-page="${i + 1}" ${i + 1 === page ? 'aria-current="page"' : ''}>${i + 1}</button>`).join('')
          + `<button class="btn sm" data-page="${page + 1}" ${page === pages ? 'disabled' : ''}>ถัดไป ›</button>`
        : '';
    },

    statusLabel(status) { return { 'Available': 'พร้อมใช้', 'In Use': 'กำลังใช้', 'Maintenance': 'ซ่อม', 'Retired': 'เลิกใช้' }[status] || status; },

    syncStatusTab(status) {
      document.querySelectorAll('#status-tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.status === status));
    },

    resetFilterControls() {
      $('search').value = ''; $('filter-category').value = '';
      document.querySelectorAll('#status-tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.status === ''));
    },

    /* ---------- Master data view ---------- */
    renderCategories(categories, usageOf, canManage) {
      $('category-list').innerHTML = categories.map((c) => `
        <li>
          <span>${esc(c)}</span>
          <span class="muted">ใช้งาน ${usageOf(c)} รายการ</span>
          <span>${canManage
            ? btn(`data-act="rename" data-id="${esc(c)}"`, '✎ แก้ไข') + ' ' + btn(`data-act="delete" data-id="${esc(c)}"`, 'ลบ')
            : ''}</span>
        </li>`).join('');
    },

    renderEmployees(employees, canManage) {
      $('emp-body').innerHTML = employees.map((e) => `
        <tr>
          <td>${esc(e.name)}</td>
          <td>${esc(e.department) || '-'}</td>
          <td class="row-actions">${canManage
            ? btn(`data-act="edit" data-id="${esc(e.id)}"`, '✎ แก้ไข') + btn(`data-act="delete" data-id="${esc(e.id)}"`, 'ลบ')
            : '-'}</td>
        </tr>`).join('') || '<tr><td colspan="3" class="feed-empty">ยังไม่มีพนักงาน</td></tr>';
    },

    /* ---------- Toast & overlays ---------- */
    toast(msg, isError = false) {
      const el = document.createElement('div');
      el.className = 'toast' + (isError ? ' error' : '');
      el.textContent = msg;
      $('toasts').appendChild(el);
      setTimeout(() => el.classList.add('out'), 3000);
      setTimeout(() => el.remove(), 3400);
    },

    closeModals,

    showDetail(asset, can, statusEnum) {
      $('drawer-title').textContent = `${asset.id} — ${asset.name}`;
      const rows = [
        ['สถานะ', `<span class="badge ${STATUS_CLASS[asset.status]}">${esc(this.statusLabel(asset.status))}</span>`],
        ['หมวดหมู่', esc(asset.category)],
        ['ผู้ถือครอง', asset.assignedTo ? esc(asset.assignedTo) : '-'],
        ['กำหนดคืน', asset.dueDate ? esc(asset.dueDate) : '-'],
        ['วันที่ซื้อ', asset.purchaseDate ? esc(asset.purchaseDate) : '-'],
      ];
      if (asset.status === statusEnum.RETIRED) rows.push(['เหตุผลเลิกใช้', esc(asset.retireReason || '-')]);
      const history = [...asset.history].reverse();
      $('drawer-body').innerHTML = `
        <dl class="kv">${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl>
        <div class="drawer-actions">${assetActions(asset, can, statusEnum, 'drawer').join('')}</div>
        <section>
          <h2>ประวัติ</h2>
          ${history.length
            ? `<ol class="timeline">${history.map((e) => `<li>${esc(e.text)}<time>${esc(fmtDate(e.ts))}</time></li>`).join('')}</ol>`
            : '<p class="feed-empty">ยังไม่มีประวัติ</p>'}
        </section>`;
      openOverlay('drawer');
    },

    showAssetForm(asset) {
      $('asset-title').textContent = asset ? 'แก้ไขอุปกรณ์' : 'เพิ่มอุปกรณ์ใหม่';
      $('f-id').value = asset ? asset.id : '';
      $('f-id').disabled = !!asset;
      $('f-name').value = asset ? asset.name : '';
      if (asset) $('f-category').value = asset.category;
      else if ($('f-category').options.length) $('f-category').selectedIndex = 0;
      $('f-date').value = asset ? asset.purchaseDate || '' : '';
      $('f-date').disabled = !!asset;
      this.setAssetError('');
      this.updateAssetSaveState();
      openOverlay('modal-asset');
      (asset ? $('f-name') : $('f-id')).focus();
    },
    getAssetForm: () => ({
      id: $('f-id').value, name: $('f-name').value,
      category: $('f-category').value, purchaseDate: $('f-date').value,
    }),
    updateAssetSaveState() {
      $('btn-save').disabled = !($('f-id').value.trim() && $('f-name').value.trim());
    },
    setAssetError(msg, field) {
      $('err-id').textContent = field === 'id' ? msg : '';
      $('asset-error').textContent = field === 'id' ? '' : msg;
    },

    /* fields: [{ name, label, type?, value?, required?, disabled?, hint?, options?: [{value,label}], showWhen?: checkboxName }]
       type: text | date | password | textarea | checkbox | info (read-only text in `text`).
       onSubmit(values) may be async and returns { ok, error?, field? }; the modal closes on ok.
       opts: { submitLabel, danger } */
    showForm(title, fields, onSubmit, opts = {}) {
      formCallback = onSubmit;
      $('gf-title').textContent = title;
      $('gf-error').textContent = '';
      const submit = $('gf-submit');
      submit.textContent = opts.submitLabel || 'บันทึก';
      submit.className = 'btn ' + (opts.danger ? 'danger' : 'primary');

      $('gf-fields').innerHTML = fields.map((f) => {
        const hidden = f.showWhen ? ' hidden' : '';
        const when = f.showWhen ? ` data-show-when="${f.showWhen}"` : '';
        if (f.type === 'info') return `<p class="info-line">${esc(f.text)}</p>`;
        if (f.type === 'checkbox') {
          return `<label class="check"><input type="checkbox" name="${f.name}"${f.value ? ' checked' : ''}> ${esc(f.label)}</label>`;
        }
        const attrs = `name="${f.name}"${f.disabled ? ' disabled' : ''}`;
        let control;
        if (f.options) {
          control = `<select ${attrs}>${f.options.map((o) => `<option value="${esc(o.value)}"${o.value === f.value ? ' selected' : ''}>${esc(o.label)}</option>`).join('')}</select>`;
        } else if (f.type === 'textarea') {
          control = `<textarea ${attrs}>${esc(f.value)}</textarea>`;
        } else {
          control = `<input ${attrs} type="${f.type || 'text'}" value="${esc(f.value)}" autocomplete="off">`;
        }
        return `<label class="field${hidden}"${when} data-field="${f.name}" data-required="${f.required ? 1 : 0}">
          <span class="field-label">${esc(f.label)}${f.required ? ' <b class="req">*</b>' : ''}</span>${control}
          ${f.hint ? `<small class="hint">${esc(f.hint)}</small>` : ''}<small class="field-error"></small></label>`;
      }).join('');

      const syncConditional = () => $('gf-fields').querySelectorAll('[data-show-when]').forEach((el) => {
        const toggle = $('gf-fields').querySelector(`[name="${el.dataset.showWhen}"]`);
        el.classList.toggle('hidden', !(toggle && toggle.checked));
      });
      $('gf-fields').onchange = syncConditional;
      syncConditional();

      openOverlay('modal-form');
      const first = $('gf-fields').querySelector('input:not([disabled]):not([type=checkbox]), select:not([disabled]), textarea');
      if (first) first.focus();
    },

    showConfirm(message, onConfirm, { title = 'ยืนยันการทำรายการ', label = 'ยืนยัน' } = {}) {
      confirmCallback = onConfirm;
      $('confirm-title').textContent = title;
      $('confirm-msg').textContent = message;
      $('btn-confirm').textContent = label;
      openOverlay('modal-confirm');
    },

    /* ---------- Event binding ---------- */
    bind(h) {
      $('form-login').addEventListener('submit', (e) => { e.preventDefault(); h.onLogin($('l-user').value, $('l-pass').value); });
      $('btn-logout').addEventListener('click', h.onLogout);
      $('btn-password').addEventListener('click', h.onChangePassword);
      $('nav').addEventListener('click', (e) => {
        const t = e.target.closest('.nav-item[data-view]');
        if (t) h.onNavigate(t.dataset.view);
      });
      $('master-tabs').addEventListener('click', (e) => {
        const t = e.target.closest('.tab');
        if (t) this.setMasterTab(t.dataset.sub);
      });

      $('btn-add').addEventListener('click', h.onAddClick);
      $('empty-add').addEventListener('click', h.onAddClick);
      $('empty-clear').addEventListener('click', h.onClearFilters);
      $('form-asset').addEventListener('submit', (e) => { e.preventDefault(); h.onAssetSubmit(); });
      $('form-asset').addEventListener('input', () => this.updateAssetSaveState());

      $('form-generic').addEventListener('submit', async (e) => {
        e.preventDefault();
        const box = $('gf-fields');
        box.querySelectorAll('.field-error').forEach((el) => { el.textContent = ''; });
        $('gf-error').textContent = '';

        const values = {};
        box.querySelectorAll('[name]').forEach((el) => { values[el.name] = el.type === 'checkbox' ? el.checked : el.value; });

        // Required fields (ignoring ones hidden by a checkbox)
        let firstBad = null;
        box.querySelectorAll('.field[data-required="1"]').forEach((wrap) => {
          if (wrap.classList.contains('hidden')) return;
          const input = wrap.querySelector('[name]');
          if (!String(input.value).trim()) {
            wrap.querySelector('.field-error').textContent = 'กรุณากรอกข้อมูลช่องนี้';
            firstBad = firstBad || input;
          }
        });
        if (firstBad) return firstBad.focus();

        const result = await formCallback(values);
        if (result.ok) return closeModals();
        const target = result.field && box.querySelector(`[data-field="${result.field}"]`);
        if (target) { target.querySelector('.field-error').textContent = result.error; target.querySelector('[name]').focus(); }
        else $('gf-error').textContent = result.error;
      });
      $('btn-confirm').addEventListener('click', () => { closeModals(); confirmCallback(); });

      document.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', closeModals));
      overlays().forEach((m) => m.addEventListener('click', (e) => { if (e.target === m) closeModals(); }));
      document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModals(); });

      $('search').addEventListener('input', (e) => h.onSearch(e.target.value));
      $('filter-category').addEventListener('change', (e) => h.onCategoryFilter(e.target.value));
      $('status-tabs').addEventListener('click', (e) => {
        const tab = e.target.closest('.tab');
        if (!tab) return;
        document.querySelectorAll('#status-tabs .tab').forEach((t) => t.classList.toggle('active', t === tab));
        h.onStatusFilter(tab.dataset.status);
      });
      $('pagination').addEventListener('click', (e) => {
        const p = e.target.closest('[data-page]');
        if (p && !p.disabled) h.onPageChange(Number(p.dataset.page));
      });

      const delegate = (containerId, handler, beforeHandle) => $(containerId).addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b || b.disabled) return;
        if (beforeHandle) beforeHandle();
        handler(b.dataset.act, b.dataset.id);
      });
      delegate('tbody', h.onRowAction);
      delegate('attention', h.onRowAction);
      delegate('recent', h.onRowAction);
      delegate('drawer-body', h.onRowAction, closeModals);   // close the drawer before opening a dialog
      delegate('category-list', h.onCategoryAction);
      delegate('emp-body', h.onEmployeeAction);
      delegate('movement-body', h.onRowAction);
      delegate('maint-body', h.onRowAction);
      delegate('users-body', h.onUserAction);
      $('btn-add-user').addEventListener('click', h.onUserCreate);
      $('btn-refresh-users').addEventListener('click', h.onRefreshUsers);
      document.querySelectorAll('[data-export]').forEach((b) => b.addEventListener('click', h.onExport));
      document.querySelectorAll('[data-goto]').forEach((b) => b.addEventListener('click', () => h.onNavigate(b.dataset.goto)));
      $('btn-add-category').addEventListener('click', () => h.onCategoryAction('add'));
      $('btn-add-employee').addEventListener('click', () => h.onEmployeeAction('add'));
    },
  };
})();




