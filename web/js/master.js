/* Model: master data — asset categories and employees. */
const MasterModel = (() => {
  const CATEGORY_KEY = 'it-categories';
  const EMPLOYEE_KEY = 'it-employees';

  let categories = Store.get(CATEGORY_KEY, ['Notebook', 'Monitor', 'Mouse', 'Keyboard', 'Printer', 'Other']);
  let employees = Store.get(EMPLOYEE_KEY, [
    { id: 'E-001', name: 'สมชาย ใจดี', department: 'ฝ่ายขาย' },
    { id: 'E-002', name: 'สมหญิง รักงาน', department: 'ฝ่ายบัญชี' },
    { id: 'E-003', name: 'วิชัย มั่นคง', department: 'ฝ่ายไอที' },
  ]);
  const listeners = [];

  function commit() {
    Store.set(CATEGORY_KEY, categories);
    Store.set(EMPLOYEE_KEY, employees);
    listeners.forEach((fn) => fn());
  }
  const hasCategory = (name, except) => categories.some((c) => c.toLowerCase() === name.toLowerCase() && c !== except);
  const findEmployee = (id) => employees.find((e) => e.id === id);

  function nextEmployeeId() {
    const max = employees.reduce((m, e) => Math.max(m, Number(e.id.replace('E-', '')) || 0), 0);
    return 'E-' + String(max + 1).padStart(3, '0');
  }

  return {
    subscribe: (fn) => listeners.push(fn),
    getCategories: () => categories.slice(),
    getEmployees: () => employees.slice(),
    findEmployee,

    addCategory(name) {
      name = name.trim();
      if (!name) return { ok: false, error: 'กรุณากรอกชื่อหมวดหมู่' };
      if (hasCategory(name)) return { ok: false, error: `หมวดหมู่ ${name} มีอยู่แล้ว` };
      categories.push(name);
      commit();
      return { ok: true };
    },
    renameCategory(oldName, newName) {
      newName = newName.trim();
      if (!newName) return { ok: false, error: 'กรุณากรอกชื่อหมวดหมู่' };
      if (hasCategory(newName, oldName)) return { ok: false, error: `หมวดหมู่ ${newName} มีอยู่แล้ว` };
      categories = categories.map((c) => (c === oldName ? newName : c));
      commit();
      return { ok: true, name: newName };
    },
    removeCategory(name) {
      if (categories.length <= 1) return { ok: false, error: 'ต้องมีหมวดหมู่อย่างน้อย 1 รายการ' };
      categories = categories.filter((c) => c !== name);
      commit();
      return { ok: true };
    },

    addEmployee({ name, department }) {
      name = name.trim();
      if (!name) return { ok: false, error: 'กรุณากรอกชื่อพนักงาน' };
      if (employees.some((e) => e.name === name)) return { ok: false, error: `พนักงาน ${name} มีอยู่แล้ว` };
      employees.push({ id: nextEmployeeId(), name, department: department.trim() });
      commit();
      return { ok: true };
    },
    updateEmployee(id, { name, department }) {
      name = name.trim();
      if (!name) return { ok: false, error: 'กรุณากรอกชื่อพนักงาน' };
      if (employees.some((e) => e.name === name && e.id !== id)) return { ok: false, error: `พนักงาน ${name} มีอยู่แล้ว` };
      Object.assign(findEmployee(id), { name, department: department.trim() });
      commit();
      return { ok: true };
    },
    removeEmployee(id) {
      employees = employees.filter((e) => e.id !== id);
      commit();
    },
  };
})();
