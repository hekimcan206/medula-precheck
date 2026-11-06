# MEDULA pre-check report / Ön kontrol raporu

Generated 2026-11-06 00:00 UTC by `dotnet-engine`. Synthetic data.

| Invoices | Clean | Needs review | Would be rejected | Findings | Total (TRY) | At risk (TRY) |
|---:|---:|---:|---:|---:|---:|---:|
| 40 | 26 | 0 | 14 | 14 | 298,373.90 | 43,765.80 |

## Findings by rule

| Rule | Kural | Rule (EN) | Count |
|---|---|---|---:|
| `CLN-002` | Hekim branşı yetkili değil | Physician specialty not authorised | 1 |
| `DEM-002` | İşlem hasta yaşına uygun değil | Procedure not valid for the patient's age | 1 |
| `DEM-003` | Tanı hasta cinsiyetine uygun değil | Diagnosis not valid for the patient's sex | 1 |
| `FRQ-002` | 10 gün içinde aynı branşta muayene | Repeat examination in the same specialty | 1 |
| `MDL-001` | Takip numarası geçersiz | Invalid provision tracking number | 1 |
| `MDL-002` | Hizmet tarihi provizyon dönemi dışında | Service date outside the provision period | 1 |
| `MDL-003` | Fatura tarihi hizmetten önce | Invoice dated before the service or discharge | 1 |
| `MDL-004` | SUT kodu bu sürümde yok | SUT code not in the version in force | 1 |
| `MDL-006` | Hekim bilgisi eksik/geçersiz | Physician missing or invalid | 1 |
| `MDL-007` | Tanı kodlaması hatalı | Diagnosis coding invalid | 1 |
| `PKG-001` | Paket kapsamındaki hizmet ayrıca faturalandırılmış | Service included in a package billed separately | 1 |
| `PKG-003` | Yatarak tedavi gerektiren işlem ayaktan faturalandırılmış | Inpatient-only service on an outpatient provision | 1 |
| `PRC-001` | Birim fiyat tarifeden farklı | Unit price differs from the tariff | 1 |
| `PRC-002` | Günlük adet sınırı aşıldı | Daily quantity limit exceeded | 1 |

## Invoices with findings

### FT2511-000001 — Rejected (SUT-2025.07, at risk 650.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `PKG-003` | Error | 4 | 904001 | Satır 4 (904001) yalnızca yatarak tedavide faturalandırılır; provizyon Outpatient.<br>_Line 4 (904001) is billable in inpatient care only; the provision is Outpatient._ | Provizyon tedavi türünü Inpatient olarak düzeltin veya hizmeti yatış provizyonuna taşıyın.<br>_Change the provision treatment type to Inpatient or move the service to the inpatient provision._ |

### FT2601-000003 — Rejected (SUT-2026.01, at risk 100.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `MDL-004` | Error | 2 | 999999 | Satır 2: 999999 kodu SUT-2026.01 sürümünde tanımlı değil.<br>_Line 2: code 999999 is not defined in SUT version SUT-2026.01._ | Kodu SUT-2026.01 listesindeki güncel karşılığıyla değiştirin; kaldırılmış kodlar faturalandırılamaz.<br>_Replace the code with its current equivalent in SUT-2026.01; withdrawn codes cannot be billed._ |

### FT2509-000005 — Rejected (SUT-2025.07, at risk 190.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `DEM-003` | Error | - | - | Tanı O80.0 yalnızca Female hastalarda kullanılabilir.<br>_Diagnosis O80.0 can only be used for Female patients._ | Tanı kodunu veya hasta cinsiyetini düzeltin.<br>_Correct the diagnosis code or the patient's recorded sex._ |

### FT2511-000008 — Rejected (SUT-2025.07, at risk 20,550.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `MDL-007` | Error | - | - | Tanı 'INVALID' ICD-10 biçimine uymuyor veya ana tanı sayısı hatalı (A00.0).<br>_Diagnosis 'INVALID' is not valid ICD-10 or the primary-diagnosis count is wrong (A00.0)._ | Tanıyı ICD-10 biçiminde (ör. E11.9) yazın ve tam olarak bir ana tanı işaretleyin.<br>_Write the diagnosis in ICD-10 form (e.g. E11.9) and mark exactly one primary diagnosis._ |

### FT2603-000010 — Rejected (SUT-2026.01, at risk 270.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `PRC-002` | Error | 1 | 900101 | Satır 1 (900101) aynı gün toplam 2 adet; günlük sınır 1.<br>_Line 1 (900101): 2 units on one day; the daily limit is 1._ | Aynı gündeki toplam adedi 1 adede indirin.<br>_Reduce the total for that day to 1 unit(s)._ |

### FT2601-000015 — Rejected (SUT-2026.01, at risk 135.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `FRQ-002` | Error | 1 | 900101 | Satır 1 (900101) muayenesi, 2025-12-26 tarihindeki aynı branş muayenesinden sonraki 10 gün içinde.<br>_Line 1 (900101): examination within 10 days of a same-specialty examination on 2025-12-26._ | Muayene satırını çıkarın; kontrol muayenesi ayrıca faturalandırılmaz (ek işlemler faturalandırılabilir).<br>_Remove the examination line; a follow-up visit is not billed again (additional procedures still are)._ |

### FT2512-000017 — Rejected (SUT-2025.07, at risk 540.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `CLN-002` | Error | 3 | 902005 | Satır 3 (902005) hekim branşı 1200; yetkili branşlar: 1600.<br>_Line 3 (902005) was performed by specialty 1200; authorised specialties: 1600._ | Satırı yetkili branştaki (1600) hekime bağlayın veya istem/konsültasyon kaydını ekleyin.<br>_Attribute the line to a physician in 1600 or attach the request/consultation record._ |

### FT2512-000026 — Rejected (SUT-2025.07, at risk 95.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `MDL-002` | Error | 1 | 900102 | Satır 1 (900102) hizmet tarihi 2025-12-02, provizyon dönemi 2025-12-03.. dışında.<br>_Line 1 (900102) is dated 2025-12-02, outside the provision period 2025-12-03..._ | Hizmet tarihini 2025-12-03.. aralığına düzeltin ya da hizmeti ait olduğu provizyona taşıyın.<br>_Correct the service date into 2025-12-03.. or move the service to the provision it belongs to._ |

### FT2510-000028 — Rejected (SUT-2025.07, at risk 130.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `PRC-001` | Error | 1 | 900101 | Satır 1 (900101) birim fiyatı 130.00 TL, geçerli tarife 120.00 TL.<br>_Line 1 (900101) is priced 130.00 TRY; the tariff in force is 120.00 TRY._ | Birim fiyatı 120.00 TL olarak düzeltin.<br>_Set the unit price to 120.00 TRY._ |

### FT2512-000031 — Rejected (SUT-2025.07, at risk 1,570.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `MDL-001` | Error | - | - | Takip numarası 'tk-93' biçim olarak geçersiz; MEDULA hizmet kaydını kabul etmez.<br>_Tracking number 'tk-93' is malformed; MEDULA will not accept the service record._ | Provizyonu MEDULA'dan yeniden sorgulayıp [0-9A-Z]{7,10} biçimindeki takip numarasını faturaya yazın.<br>_Re-query the provision in MEDULA and put the tracking number ([0-9A-Z]{7,10}) on the invoice._ |

### FT2603-000033 — Rejected (SUT-2026.01, at risk 18,600.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `MDL-006` | Error | 1 | 903008 | Satır 1 (903008) hekim tescil no '000000' faturadaki hekim listesinde yok veya geçersiz.<br>_Line 1 (903008): physician registration '000000' is not on the invoice or is malformed._ | Hizmeti yapan hekimi (\d{6} biçiminde tescil no) faturaya ekleyin veya satırı doğru hekime bağlayın.<br>_Add the performing physician (registration \d{6}) to the invoice or link the line to the right one._ |

### FT2602-000036 — Rejected (SUT-2026.01, at risk 30.80 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `PKG-001` | Error | 3 | 901001 | Satır 3 (901001) 903007 paketinin kapsamında; ayrıca faturalandırılamaz.<br>_Line 3 (901001) is included in package 903007 and cannot be billed separately._ | Satır 3'i çıkarın; bedel 903007 paket fiyatına dahildir.<br>_Remove line 3; its cost is included in the price of package 903007._ |

### FT2508-000039 — Rejected (SUT-2025.07, at risk 140.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `DEM-002` | Error | 3 | 901009 | Satır 3 (901009) -0 yaş aralığında faturalandırılır; hasta 21 yaşında.<br>_Line 3 (901009) is billable for ages -0; the patient is 21._ | Doğum tarihini doğrulayın; yaş aralığı dışındaki uygulama için tıbbi gerekçe raporu ekleyin veya satırı çıkarın.<br>_Verify the birth date; attach a medical justification for an out-of-range use or remove the line._ |

### FT2510-000040 — Rejected (SUT-2025.07, at risk 765.00 TRY)

| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |
|---|---|---:|---|---|---|
| `MDL-003` | Error | - | - | Fatura tarihi 2025-09-22, son hizmet/taburcu tarihi 2025-09-23 öncesinde.<br>_Invoice date 2025-09-22 precedes the last service / discharge date 2025-09-23._ | Faturayı 2025-09-23 veya sonrasına tarihleyin; yatan hastada taburcu olmadan fatura kesmeyin.<br>_Date the invoice on or after 2025-09-23; do not invoice an inpatient stay before discharge._ |

