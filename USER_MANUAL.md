# คู่มือการใช้งานระบบ MyLog (User Manual)
**Enterprise Log Viewer & Analytics Console**

---

## สารบัญ (Table of Contents)
1. [ภาพรวมของระบบ (Overview)](#1-ภาพรวมของระบบ-overview)
2. [การตั้งค่าคอนฟิก (Configuration)](#2-การตั้งค่าคอนฟิก-configuration)
3. [โหมดการทำงานหลัก (Operation Modes)](#3-โหมดการทำงานหลัก-operation-modes)
   - [3.1 Local Mode (ไฟล์บนเครื่อง/เครื่องแม่ข่าย)](#31-local-mode)
   - [3.2 Remote Mode (ดึงผ่าน Gateway SIT/UAT)](#32-remote-mode)
   - [3.3 Cluster Mode (รวมหลายโหนดหลัง Load Balancer)](#33-cluster-mode)
4. [การดู Log แบบ Cluster: Timeline vs Concat](#4-การดู-log-แบบ-cluster-timeline-vs-concat)
5. [การดาวน์โหลดไฟล์และโฟลเดอร์ (Downloads)](#5-การดาวน์โหลดไฟล์และโฟลเดอร์-downloads)
6. [การค้นหาและการกรองข้อมูล (Search & Filters)](#6-การค้นหาและการกรองข้อมูล-search--filters)
7. [ฟังก์ชันช่วยวิเคราะห์ Log (Advanced Tools)](#7-ฟังก์ชันช่วยวิเคราะห์-log-advanced-tools)
   - [7.1 Auto Format JSON & Expand Toggle](#71-auto-format-json--expand-toggle)
   - [7.2 Request Flow Tracker (GUID / Correlation ID)](#72-request-flow-tracker)
   - [7.3 ระบบบุ๊กมาร์ก (Bookmarks)](#73-ระบบบุ๊กมาร์ก-bookmarks)
   - [7.4 Custom Highlighting](#74-custom-highlighting)
8. [โหมดการใช้งานบนอุปกรณ์มือถือ (Mobile UX)](#8-โหมดการใช้งานบนอุปกรณ์มือถือ-mobile-ux)
9. [คำถามที่พบบ่อยและการแก้ปัญหา (FAQ & Troubleshooting)](#9-คำถามที่พบบ่อยและการแก้ปัญหา-faq--troubleshooting)

---

## 1. ภาพรวมของระบบ (Overview)
**MyLog** เป็น Web Application แบบ Lightweight พัฒนาด้วย ASP.NET Core 8 สำหรับตรวจสอบ ค้นหา และวิเคราะห์ Log ของระบบ Enterprise และ Banking (SIT, UAT, Production) 
* **Zero External Dependencies:** ทำงานได้ 100% แม้ในเครือข่ายปิด (Offline / Intranet) ไม่มี CDN ภายนอก
* **High Security:** ป้องกัน Path Traversal (`../`) อย่างรัดกุมผ่านระบบ `AllowedFolders`
* **Real-time Tail:** ตรวจสอบ Log สดๆ แบบ Real-time ผ่าน SignalR
* **Zero-RAM Temp Zip Streaming:** บีบอัดและดาวน์โหลดไฟล์ขนาดใหญ่โดยไม่กิน RAM ของเครื่องเซิร์ฟเวอร์

---

## 2. การตั้งค่าคอนฟิก (Configuration)
การตั้งค่าทั้งหมดอยู่ที่ไฟล์ `appsettings.json`:

```json
{
  "AllowedFolders": [
    "D:\\WSProjectLogs",
    "C:\\Logs"
  ],
  "RemoteLogApi": {
    "UseUatEnvironment": false,
    "SitBaseUrl": "https://al-gateway-sit.example.com/",
    "SitApiKey": "YOUR_SIT_API_KEY",
    "UatBaseUrl": "https://al-gateway-uat.example.com/",
    "UatApiKey": "YOUR_UAT_API_KEY"
  },
  "Clusters": [
    {
      "ClusterId": "app-cluster-01",
      "ClusterName": "Core Service Cluster (Load Balancer)",
      "Description": "Service nodes behind F5/LB",
      "Nodes": [
        {
          "NodeId": "node-01",
          "NodeName": "App Node 1 (192.168.1.10)",
          "Path": "\\\\192.168.1.10\\d$\\WSProjectLogs",
          "ApiUrl": "https://192.168.1.10/mylog",
          "HostHeader": "service.example.local"
        },
        {
          "NodeId": "node-02",
          "NodeName": "App Node 2 (192.168.1.11)",
          "Path": "\\\\192.168.1.11\\d$\\WSProjectLogs",
          "ApiUrl": "https://192.168.1.11/mylog",
          "HostHeader": "service.example.local"
        }
      ]
    }
  ]
}
```

---

## 3. โหมดการทำงานหลัก (Operation Modes)

ระบบมีแท็บสลับโหมดหลัก 3 โหมดที่มุมบนซ้ายของหน้าจอ:

### 3.1 Local Mode
* **วิธีใช้งาน:** เลือกแท็บ **Local**
* ใช้งานสำหรับเปิดอ่านไฟล์ Log ที่อยู่บนเครื่องแม่ข่ายโดยตรง หรือโฟลเดอร์ Network Share (UNC Path)
* สามารถเปิดโฟลเดอร์ย่อย ค้นหาไฟล์ และคลิกเลือกไฟล์เพื่อเปิดดูได้ทันที

### 3.2 Remote Mode
* **วิธีใช้งาน:** เลือกแท็บ **Remote (Gateway)**
* ระบบจะเชื่อมต่อไปยัง REST Gateway เพื่อดึงรายชื่อโปรเจกต์และไฟล์ Log
* **สลับสิ่งแวดล้อม:** สามารถเลือก **SIT** หรือ **UAT** ได้จาก Dropdown
* เมื่อคลิกโปรเจกต์ ระบบจะแสดงรายการไฟล์ สามารถคลิกเพื่อดู Log หรือดาวน์โหลดได้

### 3.3 Cluster Mode
* **วิธีใช้งาน:** เลือกแท็บ **Cluster (LB)**
* รวมศูนย์ Log จากหลายเครื่องเซิร์ฟเวอร์ที่อยู่หลัง Load Balancer
* **Live Health Check:** ระบบจะ Probe เช็คสถานะของแต่ละโหนด หากโหนดใด Offline จะแสดงสถานะสีเทา/Offline แต่โหนดที่เหลือยังสามารถดู Log ได้ตามปกติ (Fault Tolerant)

---

## 4. การดู Log แบบ Cluster: Timeline vs Concat

เมื่อเปิดไฟล์ Log ใน **Cluster Mode** จะมีแท็บ Pill ด้านบน Viewer ให้เลือกมุมมอง 2 แบบหลัก:

```
[ 🌐 All Nodes (Timeline) ]   [ 📑 All Nodes (ต่อกัน / Concat) ]   [ 💻 Node 1 ]   [ 💻 Node 2 ]
```

### 1. `🌐 All Nodes (Timeline)` (มุมมองตามเวลา)
* **ลักษณะการแสดงผล:** นำ Log จากทุก Node มารวมกัน แล้วจัดเรียงตาม **Timestamp ของแต่ละบรรทัด (Chronological)**
* **เหมาะสำหรับ:** การไล่ Transaction ที่กระจายไปตาม Load Balancer เพื่อดูเหตุการณ์ที่เกิดขึ้นไล่ตามวินาทีจากทุกเครื่องพร้อมกัน
* **การระบุตัวตน:** มี Badge สีระบุชื่อ Node กำกับไว้หน้าทุกบรรทัด เช่น `[Node 1]` `[Node 2]`

### 2. `📑 All Nodes (ต่อกัน / Concat)` (มุมมองเอามาต่อกัน)
* **ลักษณะการแสดงผล:** แสดง Log ของ **Node 1 ทั้งหมดตั้งแต่ต้นจนจบ** แล้วต่อด้วย Log ของ **Node 2 ทั้งหมด** และตามด้วย Node ถัดไปตามลำดับคอนฟิก
* **การคั่นโหนด:** มี Header แบ่งส่วนชัดเจน เช่น:
  ```text
  === Node: Node 1 (192.168.1.10) ===
  2026-09-04 10:00:00 [INFO] Node 1 - Step 1
  2026-09-04 10:00:01 [INFO] Node 1 - Step 2

  === Node: Node 2 (192.168.1.11) ===
  2026-09-04 09:00:00 [INFO] Node 2 - Step 1
  2026-09-04 09:00:02 [INFO] Node 2 - Step 2
  ```
* **ความถูกต้องของการเรียง:**
  * โหนดจะไม่มีการสลับลำดับ (Node 1 มาก่อน Node 2 เสมอ)
  * บรรทัดภายในแต่ละโหนดจะเรียงจากบนลงล่างตามลำดับบรรทัดเดิมเสมอ (ไม่กลับหัวหรือสลับลำดับ)
  * หากไฟล์มีขนาดใหญ่ ระบบใช้ **Fair-Share Water-filling** เพื่อให้ทุกโหนดได้รับการแสดงผลส่วนท้ายสุดอย่างเท่าเทียม ไม่มีโหนดใดถูกตัดทิ้งจนหายไป

### 3. `💻 [Node Name]` (มุมมองเฉพาะโหนด)
* คลิกเลือกดูเฉพาะโหนดที่ต้องการเพียงเครื่องเดียวได้ทันที

---

## 5. การดาวน์โหลดไฟล์และโฟลเดอร์ (Downloads)

| ฟังก์ชัน | Local Mode | Remote Mode | Cluster Mode |
|---|---|---|---|
| **ดาวน์โหลดไฟล์เดี่ยว** | ปุ่ม `Download` ในตาราง / Viewer Toolbar | ปุ่ม `Download` ในตาราง / Viewer Toolbar | • **ZIP:** บันเดิลทุกโหนดแยกโฟลเดอร์<br>• **Concat (.log):** โหลดเป็นไฟล์ `.log` ต่อกันก้อนเดียว |
| **ดาวน์โหลดทั้งโฟลเดอร์** | ปุ่ม `📦 Download Folder (ZIP)` | ปุ่ม `📦 Download Folder (ZIP)` บีบอัดโฟลเดอร์โปรเจกต์จาก Remote | ปุ่ม `📦 Download Folder (ZIP)` ดึงไฟล์จากทุกโหนดมารวมลงใน ZIP เดียว |

> **หมายเหตุประสิทธิภาพ:** ทุกการดาวน์โหลดทำงานผ่าน **Zero-RAM Temp Zip Streaming** โดยระบบจะสร้างไฟล์ชั่วคราวระดับ OS พร้อมตั้งค่าลบตัวเองอัตโนมัติเมื่อส่งข้อมูลเสร็จ ทำให้เซิร์ฟเวอร์ไม่เกิด Memory Leak หรือ LOH Spike

---

## 6. การค้นหาและการกรองข้อมูล (Search & Filters)

### 6.1 Quick Filters (พรีเซ็ตสำเร็จรูป)
คลิกเลือกจาก Dropdown ตัวกรองด่วน:
* 💥 **Errors & Exceptions Only:** ดึงเฉพาะ Error/Exception จริงๆ (ตัด JSON ที่มี `'error': null` ใน INFO ออก)
* ⚠️ **Warnings:** กรองเฉพาะ Warning
* 🌐 **HTTP 4xx / 5xx:** กรองหารหัสข้อผิดพลาด HTTP Status
* ⏱️ **Slow Requests (>1s):** ค้นหารายการที่ใช้เวลาประมวลผลนานเกิน 1 วินาที
* 🆔 **Trace ID / GUID:** ค้นหาบรรทัดที่มี GUID/UUID
* 🔌 **Timeouts & Sockets:** กรองหา Connection Timeout, Socket Error

### 6.2 Search & Regex Search
* พิมพ์คำค้นหาในช่อง `Search...`
* กดปุ่ม `.*` เพื่อเปิดโหมด **Regular Expression**
* คำที่ตรงกับการค้นหาจะถูก **Highlight** ด้วยพื้นหลังสีเหลืองเด่นชัด ทั้งในโหมด Local, Remote และ Cluster

### 6.3 Folder Search (Grep in Folder)
* กดปุ่ม `📁 In Folder` ใน Log Viewer หรือปุ่ม `🔍 Search in folder` ใน File Browser
* ระบบจะทำการสแกนค้นหาข้อความจากทุกไฟล์ในโฟลเดอร์ (และทุกโหนดใน Cluster) พร้อมแสดงผลรายการที่พบ
* คลิกที่ผลการค้นหาเพื่อเปิดไฟล์และกระโดดไปยังบรรทัดนั้นทันที

### 6.4 Time-Range Filter
* ระบุ `Start Time` (เวลาเริ่มต้น) และ `End Time` (เวลาสิ้นสุด) เช่น `14:00:00` ถึง `14:30:00`
* ระบบจะกรองแสดงเฉพาะ Log ในช่วงเวลาที่เลือกโดยอัตโนมัติ

---

## 7. ฟังก์ชันช่วยวิเคราะห์ Log (Advanced Tools)

### 7.1 Auto Format JSON & Expand Toggle
* **Checkbox `Auto Format JSON`:** 
  * เมื่อเปิดใช้งาน: ระบบจะค้นหาก้อน JSON ภายในบรรทัด Log และจัด Indent ขึ้นบรรทัดใหม่อย่างสวยงามให้อัตโนมัติ
  * เมื่อปิดใช้งาน: จะแสดง JSON ดิบเป็นบรรทัดเดียวตามต้นฉบับ
* **ปุ่มคลิกขยาย/ย่อบนบรรทัด:**
  * สามารถคลิกที่ตัวก้อน JSON เพื่อสลับย่อ-ขยายเฉพาะบรรทัดนั้นๆ ได้

### 7.2 Request Flow Tracker (GUID / Correlation ID)
* ระบบจะตรวจจับ `RequestUid`, `CorrelationId`, และ GUID อัตโนมัติ และแสดงเป็น **Clickable Chip**
* เมื่อคลิกที่ Chip:
  * ระบบจะดึงและไฮไลต์เฉพาะบรรทัดทั้งหมดที่อยู่ใน Flow ธุรกรรมเดียวกัน
  * มีแถบ Banner แสดงจำนวนขั้นตอนในธุรกรรม และปุ่มคัดลอกสรุป Flow ไปยัง Clipboard

### 7.3 ระบบบุ๊กมาร์ก (Bookmarks)
* คลิกที่ไอคอนดาว `☆` หน้าเลขบรรทัดเพื่อปักหมุดบรรทัดสำคัญ
* เปิดแถบ **Bookmarks Drawer** ด้านข้างเพื่อดูรายการที่ปักหมุดไว้
* สามารถกดปุ่มเพื่อกระโดดไปยังบรรทัดนั้น หรือกด **Export Summary** เพื่อนำสรุปเหตุการณ์ไปแปะใน Teams หรือ JIRA

### 7.4 Custom Highlighting
* ใส่คำค้นหาที่ต้องการเน้นสีในช่อง Custom Highlighter (คั่นด้วยจุลภาค `,`) เช่น: `Exception, NullReference, Timeout`
* ระบบจะใส่สีไฮไลต์แยกตามกลุ่มคำให้อัตโนมัติ

---

## 8. โหมดการใช้งานบนอุปกรณ์มือถือ (Mobile UX)

เมื่อเปิดใช้งานบน Smartphone หรือ Tablet ระบบจะปรับเป็น **Mobile-First Console** โดยอัตโนมัติ:
* **Bottom Command Bar:** เมนูควบคุมหลักอยู่ด้านล่างหน้าจอ สะดวกต่อการใช้งานมือเดียว
  * ปุ่ม `🚨 Errors`: กรอง Error ได้ใน 1 แตะ
  * ปุ่ม `↩️ Wrap`: สลับการตัดบรรทัด (Word Wrap) กับ Terminal Mode กว้างเต็มจอ
  * ปุ่ม `🔍 Search`: เปิดช่องค้นหาด่วน
  * ปุ่ม `⭐ Pinned`: เปิดดูบุ๊กมาร์ก
  * ปุ่ม `📊 Stats`: เปิดดู Dashboard กราฟและสถิติ
* **Tap-to-Inspect Bottom Sheet:** แตะที่บรรทัด Log ใดๆ เพื่อเปิด Drawer ดูรายละเอียด, วันเวลา, GUID และกดคัดลอกได้ทันที

---

## 9. คำถามที่พบบ่อยและการแก้ปัญหา (FAQ & Troubleshooting)

#### Q1: ทำไมเปิดดู Cluster แบบ Concat แล้วเห็นแต่โหนดอื่น ไม่เห็นโหนดที่ 1?
**คำตอบ:** 
ในเวอร์ชันก่อนหน้า หากไฟล์มีขนาดใหญ่เกินขีดจำกัดหน้าจอ (3,000 บรรทัด) ระบบเดิมอาจตัดทิ้งส่วนหัว 
**แต่ในเวอร์ชันปัจจุบันได้รับการแก้ไขแล้ว:** ระบบจะใช้ **Fair-Share Allocation** ทำให้ทุกโหนดจะได้รับการจัดสรรโควต้าแสดงผลบรรทัดล่าสุดอย่างเป็นธรรม และ Node 1 จะแสดงอยู่ด้านบนสุดเสมอ

#### Q2: โหนดใน Cluster ตัวหนึ่งดับไป ระบบจะค้างหรือไม่?
**คำตอบ:**
ไม่ค้างแน่นอน ระบบมี Built-in Timeout (2-5 วินาที) หากโหนดใดไม่ตอบสนอง โหนดนั้นจะถูกขึ้นสถานะ `(Offline)` แต่โหนดอื่นๆ ที่ยังทำงานอยู่จะสามารถดู Log, รวม Timeline, หรือ Concat ได้ตามปกติ

#### Q3: ต้องการบันทึก Log ทั้งหมดของ Cluster เก็บไว้ทำอย่างไร?
**คำตอบ:**
* หากต้องการแยกโฟลเดอร์ตามแต่ละเครื่อง: คลิกปุ่ม **📦 Download ZIP**
* หากต้องการไฟล์ `.log` ข้อความก้อนเดียวที่ต่อกัน: คลิกปุ่ม **📑 Download Concat (.log)**

---
*เอกสารนี้จัดทำขึ้นสำหรับ MyLog Enterprise Console — ปรับปรุงล่าสุด กันยายน 2026*
