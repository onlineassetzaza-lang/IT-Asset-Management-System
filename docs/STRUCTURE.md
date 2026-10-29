# IT Asset Management — Project Structure & Design Brief

เอกสารนี้สรุปโครงสร้างโปรเจกต์ โมเดลข้อมูล API สิทธิ์ และข้อมูลที่แต่ละหน้าจอต้องใช้ เพื่อใช้ออกแบบ UI/UX ต่อ
สถานะของแต่ละส่วนมีระบุไว้ (✅ มีแล้ว · 🔶 มี API แต่หน้าเว็บยังไม่ต่อ · ⬜ ยังไม่ทำ)

## 1. ภาพรวม

```text
Browser ──HTTP──▶ ASP.NET Core API (เสิร์ฟหน้าเว็บ + REST /api/v1) ──▶ SQLite (storage/itam.db)
```

- API และหน้าเว็บอยู่ origin เดียวกัน (ไม่ต้องตั้ง CORS) — เปิดที่ `http://localhost:5080`
- ล็อกอินด้วย cookie session; การตรวจสิทธิ์จริงอยู่ที่ API หน้าเว็บแค่ซ่อน/แสดงปุ่ม

## 2. โครงสร้างโฟลเดอร์

```text
IT-Asset-Management-System/
├── docs/STRUCTURE.md          ← เอกสารนี้
├── web/                       ← หน้าเว็บ (HTML/CSS/JS ล้วน, MVC)
│   ├── index.html             หน้าเดียว: login, แอป, modal ทั้งหมด
│   ├── style.css
│   └── js/
│       ├── store.js           ตัวช่วย localStorage/sessionStorage
│       ├── api.js             ตัวเรียก REST API (ใส่ header CSRF ให้)
│       ├── auth.js            Model: ผู้ใช้ที่ล็อกอิน (ผ่าน API) ✅
│       ├── master.js          Model: หมวดหมู่/พนักงาน (ยังเป็น localStorage) 🔶
│       ├── model.js           Model: อุปกรณ์ (ยังเป็น localStorage) 🔶
│       ├── view.js            View: วาด DOM + จับ event
│       └── controller.js      Controller: เชื่อม View ↔ Model, เช็กสิทธิ์
└── api/
    ├── README.md              วิธีรัน, ตารางสถานะ, ตารางสิทธิ์
    ├── src/ItAssetApi/
    │   ├── Program.cs         ตั้งค่า auth, CSRF, pipeline, map endpoints
    │   ├── Domain/            Entities (User, Asset, Assignment, ...)
    │   ├── Data/              AppDb (EF Core), Bootstrap (seed admin/หมวดหมู่)
    │   ├── Security/          Policies (สิทธิ์ตาม Role), PasswordPolicy
    │   ├── Auth/              login/logout/me/เปลี่ยนรหัสผ่าน, จัดการผู้ใช้
    │   ├── Inventory/         assets, categories, locations, employees, dashboard
    │   ├── Assignments/       check-out / check-in
    │   ├── Maintenance/       เปิด/ปิดงานซ่อม
    │   └── Audit/             AuditService + GET /audit-events
    └── tests/ItAssetApi.Tests/  17 เทสต์ตามเกณฑ์ยอมรับ MVP
```

## 3. บทบาทและสิทธิ์

| สิทธิ์ | Admin | IT Staff | Auditor |
|---|:-:|:-:|:-:|
| ดู Dashboard / อุปกรณ์ / พนักงาน | ✅ | ✅ | ✅ |
| เพิ่ม/แก้ไข/เลิกใช้อุปกรณ์, จ่าย–รับคืน, งานซ่อม | ✅ | ✅ | — |
| จัดการพนักงาน | ✅ | ✅ | — |
| จัดการหมวดหมู่/สถานที่ | ✅ | — | — |
| จัดการผู้ใช้และ Role | ✅ | — | — |
| ดู Audit Log | ✅ | — | ✅ |

กติกา UI: ปุ่มที่ไม่มีสิทธิ์ให้ **ซ่อน** (ไม่ใช่แค่ disable) · ผู้ใช้ที่ถูกปิดบัญชีจะถูกเด้งออกทันที (API ตอบ 401 → กลับหน้า Login)

## 4. สถานะอุปกรณ์ (state machine)

| สถานะ | ความหมาย | สีที่ใช้อยู่ | ทำอะไรต่อได้ |
|---|---|---|---|
| `Available` | พร้อมใช้ ไม่มีผู้ถือ | เขียว `#16a34a` | จ่าย, ส่งซ่อม, เลิกใช้ |
| `InUse` | มีผู้ถือครอง | ส้ม `#ea580c` | รับคืน |
| `Maintenance` | ตรวจ/ซ่อมอยู่ | เหลือง `#ca8a04` | ปิดงานซ่อม (→ Available หรือ Retired) |
| `Retired` | เลิกใช้ (ปลายทาง) | เทา (เสนอ `#6b7280`) | ดูประวัติอย่างเดียว |

```text
Available ──check-out──▶ InUse ──check-in──▶ Available
    │                      └─check-in (ชำรุด)─▶ Maintenance
    ├──เปิดงานซ่อม────────────────────────────▶ Maintenance ──ปิดงาน──▶ Available / Retired
    └──retire (ต้องระบุเหตุผล)──▶ Retired
```

เหตุผลเลิกใช้: `Lost`, `Stolen`, `Disposed`, `EndOfLife`, `Other` · ไม่มีการลบอุปกรณ์ถาวร (ใช้ Retire แทน)

## 5. โมเดลข้อมูล

| ตาราง | ฟิลด์หลัก |
|---|---|
| `Asset` | assetTag (ไม่ซ้ำ, ตัวพิมพ์ใหญ่), name, categoryId, serialNumber?, purchaseDate?, status, locationId?, notes?, version, retireReason?, retiredAt? |
| `Assignment` | assetId, employeeId, checkedOutAt, dueAt?, conditionOut?, checkedInAt?, conditionIn?, notes? (active = ยังไม่มี checkedInAt, มีได้ 1 ต่ออุปกรณ์) |
| `MaintenanceRecord` | assetId, provider?, issue, openedAt, closedAt?, cost?, resolution? |
| `Category` / `Location` | name (ไม่ซ้ำ), description?, active |
| `Employee` | employeeNumber (ไม่ซ้ำ), displayName, department?, email?, active |
| `User` | username, displayName, role, employeeId?, active |
| `AuditEvent` | occurredAt, actor, action, targetType, targetId, outcome, metadata (เฉพาะฟิลด์ที่อนุญาต) |

## 6. API (`/api/v1`)

| กลุ่ม | Endpoint | หมายเหตุ |
|---|---|---|
| Auth | `POST /auth/login` `POST /auth/logout` `GET /auth/me` `POST /auth/change-password` | login ผิด 5 ครั้งล็อก 15 นาที (429) |
| Assets | `GET /assets?query&status&categoryId&locationId&page&pageSize` · `POST /assets` · `GET /assets/{id}` · `PATCH /assets/{id}` · `POST /assets/{id}/retire` · `GET /assets/{id}/history` | PATCH ต้องส่ง `version`; ไม่รับ `status` |
| จ่าย–รับคืน | `POST /assets/{id}/check-outs` (`employeeId`, `dueAt?`, `conditionOut?`, `requestKey`) · `POST /assets/{id}/check-ins` (`conditionIn?`, `needsMaintenance?`, `notes?`, `requestKey`) | `requestKey` 8–64 ตัวอักษร กันส่งซ้ำ |
| Maintenance | `POST /assets/{id}/maintenance` · `GET /maintenance?open=` · `POST /maintenance/{id}/close` | ปิดงานเลือก `outcome`: Available / Retired |
| Master | `GET/POST/PATCH /categories` `/locations` `/employees` | ปิดใช้งานไม่ได้ถ้ายังมีอุปกรณ์/การถือครองอยู่ (409) |
| Dashboard | `GET /dashboard/summary` | total, available, inUse, maintenance, retired, overdue, openMaintenance, byCategory[] |
| Users | `GET/POST/PATCH /users` · `POST /users/{id}/reset-password` | Admin เท่านั้น; เปลี่ยน Role/ปิดบัญชี/รีเซ็ตรหัสต้องยืนยันรหัสผ่านตัวเอง |
| Audit | `GET /audit-events?actor&action&from&to&targetId&page` | Admin และ Auditor |

รูปแบบ response รายการ: `{ items: [], total, page, pageSize }` (pageSize สูงสุด 100)
รหัสผิดพลาด: `401` ยังไม่ล็อกอิน · `403` ไม่มีสิทธิ์ · `409` ชนกัน/สถานะไม่ถูกต้อง/ข้อมูลซ้ำ · `422` กรอกไม่ครบ/ผิดรูปแบบ (มี `errors` รายฟิลด์) · `429` ล็อกบัญชีชั่วคราว
ทุกข้อความผิดพลาดที่ API ส่งมาเป็นภาษาไทยพร้อมแสดงให้ผู้ใช้ได้ทันที

## 7. หน้าจอและข้อมูลที่ต้องใช้

| หน้า | สถานะ | องค์ประกอบ / ข้อมูล |
|---|---|---|
| **Login** | ✅ | ชื่อผู้ใช้, รหัสผ่าน, ข้อความผิดพลาด (401 / 429) |
| **Dashboard** | ✅ (ข้อมูลยัง local) | การ์ด 4 ใบ (Total/Available/In Use/Maintenance) + กราฟสัดส่วนหมวดหมู่; เพิ่มได้: Retired, เกินกำหนดคืน (`overdue`), งานซ่อมที่เปิดอยู่ |
| **อุปกรณ์ (ตาราง)** | ✅ (ข้อมูลยัง local) | คอลัมน์: Asset ID, ชื่อ, หมวด, ผู้ถือ (`holder`, ว่าง = `-`), สถานะ (badge), Actions · ค้นหา, กรองหมวด, แท็บสถานะ, แบ่งหน้า 10 ต่อหน้า |
| **ฟอร์มเพิ่ม/แก้ไขอุปกรณ์** | ✅ | รหัส*, ชื่อ*, หมวดหมู่, วันที่ซื้อ (+ API รองรับ serial, สถานที่, หมายเหตุ); ตอนแก้ รหัสล็อกไว้ |
| **จ่ายอุปกรณ์** | ✅ (ยังพิมพ์ชื่อ) | ควรเป็น: เลือกพนักงาน*, วันกำหนดคืน, สภาพก่อนจ่าย |
| **รับคืน** | ✅ (ปุ่มเดียว) | ควรเป็น: สภาพหลังคืน, หมายเหตุ, ตัวเลือก "ชำรุด/ส่งซ่อม" |
| **รายละเอียดอุปกรณ์ + ประวัติ** | ⬜ | ข้อมูลอุปกรณ์เต็ม, timeline การถือครอง (`history.assignments`) และงานซ่อม (`history.maintenance`) |
| **Maintenance** | ⬜ | รายการงานซ่อมเปิด/ปิด, ฟอร์มเปิดงาน (อาการ*, ผู้ให้บริการ), ฟอร์มปิดงาน (ผลซ่อม, ค่าใช้จ่าย, ผลลัพธ์) |
| **ข้อมูลหลัก** | ✅ (ยัง local) | หมวดหมู่ (พร้อมจำนวนที่ใช้), พนักงาน (ชื่อ, แผนก) · ⬜ สถานที่ |
| **ผู้ใช้และสิทธิ์** | ✅ | ตารางผู้ใช้, เพิ่ม/แก้ไขบทบาท, เปิด/ปิดบัญชี, รีเซ็ตรหัสผ่าน และตารางสิทธิ์ของ Admin / IT Staff / Auditor (ใช้ `/users` API; Admin เท่านั้น) |
| **Audit Log** | ⬜ | ตาราง เวลา/ผู้กระทำ/การกระทำ/เป้าหมาย/ผลลัพธ์ + ตัวกรองวันที่และผู้กระทำ (อ่านอย่างเดียว) |
| **Reports** | ⬜ | ยังไม่มี API |
| **ทั่วไป** | ✅ | Toast แจ้งผล, Modal ยืนยันก่อนลบ/ทำรายการสำคัญ, เปลี่ยนรหัสผ่าน (ปุ่ม 🔑), เมนูด้านข้าง: ภาพรวม / อุปกรณ์ / เบิก–คืน / ซ่อมบำรุง / ตั้งค่าระบบ / ผู้ใช้และสิทธิ์ |

## 8. แนวทางหน้าตาที่ใช้อยู่ตอนนี้ (design tokens)

| รายการ | ค่า |
|---|---|
| พื้นหลัง / การ์ด / เส้นขอบ | `#f4f6fa` / `#ffffff` / `#e5e7eb` |
| ตัวอักษร / ตัวอักษรรอง | `#1f2937` / `#6b7280` |
| สีหลัก (ปุ่ม, แท็บที่เลือก) | `#2563eb` |
| สถานะ: เขียว / ส้ม / เหลือง / แดง | `#16a34a` / `#ea580c` / `#ca8a04` / `#dc2626` |
| รูปร่าง | การ์ดและ modal มุมมน 12px, ปุ่ม/ช่อง 8px, badge แบบแคปซูล |
| ฟอนต์ | Segoe UI / Noto Sans Thai |
| เลย์เอาต์ | เนื้อหากว้างสูงสุด 1,100px, การ์ดสรุปแบบ grid ปรับตามความกว้าง, ตารางเลื่อนแนวนอนบนจอแคบ |

## 9. กติกา UX ที่ควรคงไว้

- ปุ่ม "ยืม" กดได้เฉพาะ `Available`, "คืน" กดได้เฉพาะ `InUse`
- ปุ่มบันทึกในฟอร์มเพิ่มอุปกรณ์ปิดไว้จนกว่ากรอกรหัสและชื่อ
- ทำรายการสำเร็จ → Toast; ผิดพลาด → แสดงข้อความจาก API ในฟอร์ม (422) หรือ Toast (409)
- 409 ชนกัน ("ข้อมูลถูกแก้ไขโดยผู้อื่น") ควรมีทางให้ผู้ใช้โหลดข้อมูลใหม่
- ตัวเลขการเงิน (เช่น ค่าซ่อม) ใช้คอมมาคั่นหลักพันและทศนิยม 2 ตำแหน่ง
- สถานะว่าง/โหลด/ผิดพลาดของทุกตารางควรมีดีไซน์ (ตอนนี้มีแค่ "ไม่พบอุปกรณ์")

## 10. สิ่งที่ยังไม่ได้ทำ

ต่อหน้าอุปกรณ์/ข้อมูลหลักเข้ากับ API จริง · หน้า Audit และ Reports · MFA/SSO · EF migrations · งานสำรองข้อมูล

