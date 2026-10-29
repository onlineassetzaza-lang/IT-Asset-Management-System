/* Model: the signed-in user. Authentication and the real permission checks happen on the server;
   can() only decides which controls the UI shows. */
const AuthModel = (() => {
  // Server permission -> UI permissions
  const UI_PERMISSIONS = {
    AssetWrite: ['asset.create', 'asset.edit', 'asset.checkout', 'asset.checkin', 'asset.retire', 'maintenance.manage'],
    EmployeeManage: ['master.manage'],
    UserManage: ['user.manage'],
    // 'user.manage' is not exposed yet: the user admin page is not built
  };
  const ROLES = {
    Admin: { label: 'Admin' },
    ITStaff: { label: 'IT Staff' },
    Auditor: { label: 'Auditor' },
  };

  let me = null;
  let expired = false;   // set when the server says the session is gone
  const listeners = [];
  const notify = () => listeners.forEach((fn) => fn());

  function setUser(profile) {
    if (profile) expired = false;
    me = profile && {
      ...profile,
      uiPermissions: profile.permissions.flatMap((p) => UI_PERMISSIONS[p] || []),
    };
    notify();
  }

  return {
    ROLES,
    /** True once after a session expiry, so the login page can explain why. */
    consumeExpired() { const e = expired; expired = false; return e; },
    subscribe: (fn) => listeners.push(fn),
    current: () => me,
    can: (perm) => !!me && me.uiPermissions.includes(perm),

    /** Resume an existing session (the cookie survives page reloads). */
    async restore() {
      const res = await Api.get('/auth/me');
      setUser(res.ok ? res.data : null);
    },

    async login(username, password) {
      const res = await Api.post('/auth/login', { username, password });
      if (res.ok) setUser(res.data);
      return res;
    },

    async logout() {
      await Api.post('/auth/logout');
      setUser(null);
    },

    expire() { if (me) { expired = true; setUser(null); } },

    changeOwnPassword: (currentPassword, newPassword) =>
      Api.post('/auth/change-password', { currentPassword, newPassword }),
  };
})();

// Any 401 after sign-in means the session ended (timeout, deactivation, password change elsewhere)
Api.onUnauthorized = () => AuthModel.expire();



