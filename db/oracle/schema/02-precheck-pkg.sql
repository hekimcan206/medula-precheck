-- Set-based bulk pre-check. One INSERT ... SELECT evaluates every staged invoice against the SUT version
-- valid on its invoice date, for the rules that are joins/aggregates over reference data (see ADR 0002).
-- Value formats (dates YYYY-MM-DD, money 0.00, BINARY-sorted lists joined by ", ") are byte-identical to the
-- .NET engine; parity tests enforce that.
CREATE OR REPLACE PACKAGE precheck_pkg AUTHID DEFINER AS
    -- Rules implemented here; the .NET side uses the same list to run its parity subset.
    c_rules CONSTANT VARCHAR2(200) :=
        'MDL-002,MDL-004,MDL-005,PRC-001,PRC-002,DEM-001,DEM-002,CLN-001,CLN-002,FRQ-001,PKG-001,PKG-002,PKG-003';

    FUNCTION implemented_rules RETURN VARCHAR2;

    -- Checks everything currently in the stg_* tables of this session, records the run and its findings
    -- (audit) and returns the findings ordered by invoice, line and rule.
    PROCEDURE run_batch(p_run_id OUT NUMBER, p_findings OUT SYS_REFCURSOR);
END precheck_pkg;
/
CREATE OR REPLACE PACKAGE BODY precheck_pkg AS
    FUNCTION implemented_rules RETURN VARCHAR2 IS
    BEGIN
        RETURN c_rules;
    END implemented_rules;

    PROCEDURE run_batch(p_run_id OUT NUMBER, p_findings OUT SYS_REFCURSOR) IS
        v_run_id   NUMBER;
        v_invoices NUMBER;
        v_lines    NUMBER;
        v_findings NUMBER;
    BEGIN
        SELECT COUNT(*) INTO v_invoices FROM stg_invoice;
        SELECT COUNT(*) INTO v_lines FROM stg_line;

        INSERT INTO precheck_run (started_at, invoice_count, line_count, executed_by)
        VALUES (SYSTIMESTAMP, v_invoices, v_lines, SYS_CONTEXT('USERENV', 'SESSION_USER'))
        RETURNING run_id INTO v_run_id;

        INSERT INTO precheck_finding
            (run_id, invoice_no, line_no, rule_id, severity, sut_code, expected_value, actual_value)
        WITH inv AS (
            -- "SUT version valid on the invoice date": inclusive start, exclusive end.
            SELECT i.*, v.version_id
              FROM stg_invoice i
              JOIN sut_version v
                ON i.invoice_date >= v.effective_from
               AND (v.effective_to IS NULL OR i.invoice_date < v.effective_to)
        ),
        ln AS (
            SELECT l.invoice_no, l.line_no, l.sut_code, l.service_date, l.quantity, l.unit_price, l.doctor_reg_no,
                   inv.version_id, inv.treatment_type, inv.gender, inv.birth_date,
                   inv.admission_date, inv.discharge_date,
                   p.code AS known_code, p.kind, p.price, p.gender AS required_gender,
                   p.min_age, p.max_age, p.max_per_day, p.freq_count, p.freq_days, p.inpatient_only
              FROM stg_line l
              JOIN inv ON inv.invoice_no = l.invoice_no
              LEFT JOIN sut_procedure p ON p.version_id = inv.version_id AND p.code = l.sut_code
        ),
        candidates AS (
            -- MDL-002 service date outside the provision period
            SELECT invoice_no, line_no, 'MDL-002' AS rule_id, sut_code, version_id,
                   TO_CHAR(admission_date, 'YYYY-MM-DD') || '..' || TO_CHAR(discharge_date, 'YYYY-MM-DD') AS expected_value,
                   TO_CHAR(service_date, 'YYYY-MM-DD') AS actual_value
              FROM ln
             WHERE service_date < admission_date OR service_date > discharge_date
            UNION ALL
            -- MDL-004 code unknown in the version in force
            SELECT invoice_no, line_no, 'MDL-004', sut_code, version_id, version_id, sut_code
              FROM ln
             WHERE known_code IS NULL
            UNION ALL
            -- MDL-005 duplicate line: same code, day and physician as an earlier line
            SELECT invoice_no, line_no, 'MDL-005', sut_code, version_id, TO_CHAR(first_line), NULL
              FROM (SELECT ln.*, MIN(line_no) OVER (PARTITION BY invoice_no, sut_code, service_date, doctor_reg_no) AS first_line
                      FROM ln)
             WHERE line_no > first_line
            UNION ALL
            -- PRC-001 unit price differs from the tariff of the version
            SELECT invoice_no, line_no, 'PRC-001', sut_code, version_id,
                   TO_CHAR(price, 'FM999999999990.00'), TO_CHAR(unit_price, 'FM999999999990.00')
              FROM ln
             WHERE known_code IS NOT NULL AND unit_price <> price
            UNION ALL
            -- PRC-002 daily quantity limit
            SELECT invoice_no, line_no, 'PRC-002', sut_code, version_id, TO_CHAR(max_per_day), TO_CHAR(day_total)
              FROM (SELECT ln.*, SUM(quantity) OVER (PARTITION BY invoice_no, sut_code, service_date) AS day_total
                      FROM ln)
             WHERE max_per_day IS NOT NULL AND day_total > max_per_day
            UNION ALL
            -- DEM-001 procedure restricted to one sex
            SELECT invoice_no, line_no, 'DEM-001', sut_code, version_id, required_gender, gender
              FROM ln
             WHERE required_gender IS NOT NULL AND required_gender <> gender
            UNION ALL
            -- DEM-002 age range; completed years via YYYYMMDD arithmetic (exact for 29 February)
            SELECT invoice_no, line_no, 'DEM-002', sut_code, version_id,
                   TO_CHAR(min_age) || '-' || TO_CHAR(max_age), TO_CHAR(age)
              FROM (SELECT ln.*,
                           FLOOR((TO_NUMBER(TO_CHAR(service_date, 'YYYYMMDD'))
                                  - TO_NUMBER(TO_CHAR(birth_date, 'YYYYMMDD'))) / 10000) AS age
                      FROM ln
                     WHERE min_age IS NOT NULL OR max_age IS NOT NULL)
             WHERE age < min_age OR age > max_age
            UNION ALL
            -- CLN-001 indication: at least one diagnosis must start with a required prefix
            SELECT ln.invoice_no, ln.line_no, 'CLN-001', ln.sut_code, ln.version_id,
                   (SELECT LISTAGG(x.icd_prefix, ', ') WITHIN GROUP (ORDER BY NLSSORT(x.icd_prefix, 'NLS_SORT=BINARY'))
                      FROM sut_procedure_icd x
                     WHERE x.version_id = ln.version_id AND x.code = ln.sut_code),
                   NULL
              FROM ln
             WHERE EXISTS (SELECT 1 FROM sut_procedure_icd x WHERE x.version_id = ln.version_id AND x.code = ln.sut_code)
               AND NOT EXISTS (
                   SELECT 1
                     FROM sut_procedure_icd x
                     JOIN stg_diagnosis d
                       ON d.invoice_no = ln.invoice_no
                      AND SUBSTR(d.icd_code, 1, LENGTH(x.icd_prefix)) = x.icd_prefix
                    WHERE x.version_id = ln.version_id AND x.code = ln.sut_code)
            UNION ALL
            -- CLN-002 performing physician's specialty must be authorised (unknown physician is MDL-006, .NET)
            SELECT ln.invoice_no, ln.line_no, 'CLN-002', ln.sut_code, ln.version_id,
                   (SELECT LISTAGG(b.branch_code, ', ') WITHIN GROUP (ORDER BY NLSSORT(b.branch_code, 'NLS_SORT=BINARY'))
                      FROM sut_procedure_branch b
                     WHERE b.version_id = ln.version_id AND b.code = ln.sut_code),
                   doc.branch_code
              FROM ln
              JOIN stg_doctor doc ON doc.invoice_no = ln.invoice_no AND doc.reg_no = ln.doctor_reg_no
             WHERE EXISTS (SELECT 1 FROM sut_procedure_branch b WHERE b.version_id = ln.version_id AND b.code = ln.sut_code)
               AND NOT EXISTS (SELECT 1 FROM sut_procedure_branch b
                                WHERE b.version_id = ln.version_id AND b.code = ln.sut_code AND b.branch_code = doc.branch_code)
            UNION ALL
            -- FRQ-001 rolling-window frequency limit over prior history plus earlier lines of this invoice
            SELECT invoice_no, line_no, 'FRQ-001', sut_code, version_id,
                   TO_CHAR(freq_count) || '/' || TO_CHAR(freq_days), TO_CHAR(window_count)
              FROM (SELECT ln.*,
                           NVL((SELECT SUM(p.quantity)
                                  FROM stg_prior p
                                 WHERE p.invoice_no = ln.invoice_no
                                   AND p.sut_code = ln.sut_code
                                   AND p.service_date BETWEEN ln.service_date - (ln.freq_days - 1) AND ln.service_date), 0)
                         + (SELECT SUM(o.quantity)
                              FROM stg_line o
                             WHERE o.invoice_no = ln.invoice_no
                               AND o.sut_code = ln.sut_code
                               AND o.service_date >= ln.service_date - (ln.freq_days - 1)
                               AND (o.service_date < ln.service_date
                                    OR (o.service_date = ln.service_date AND o.line_no <= ln.line_no))) AS window_count
                      FROM ln
                     WHERE ln.freq_count IS NOT NULL)
             WHERE window_count > freq_count
            UNION ALL
            -- PKG-001 service absorbed by a package billed on the same invoice (lowest package code wins)
            SELECT ln.invoice_no, ln.line_no, 'PKG-001', ln.sut_code, ln.version_id, MIN(pk.sut_code), NULL
              FROM ln
              JOIN stg_line pk ON pk.invoice_no = ln.invoice_no
              JOIN sut_procedure pp ON pp.version_id = ln.version_id AND pp.code = pk.sut_code AND pp.kind = 'Package'
              JOIN sut_package_content c
                ON c.version_id = ln.version_id AND c.package_code = pk.sut_code AND c.included_code = ln.sut_code
             GROUP BY ln.invoice_no, ln.line_no, ln.sut_code, ln.version_id
            UNION ALL
            -- PKG-002 combined-billing ban: secondary code on the same day as its primary
            SELECT ln.invoice_no, ln.line_no, 'PKG-002', ln.sut_code, ln.version_id, MIN(pr.primary_code), NULL
              FROM ln
              JOIN sut_incompatible_pair pr ON pr.version_id = ln.version_id AND pr.secondary_code = ln.sut_code
             WHERE EXISTS (SELECT 1 FROM stg_line o
                            WHERE o.invoice_no = ln.invoice_no
                              AND o.sut_code = pr.primary_code
                              AND o.service_date = ln.service_date)
             GROUP BY ln.invoice_no, ln.line_no, ln.sut_code, ln.version_id
            UNION ALL
            -- PKG-003 inpatient-only service on a non-inpatient provision
            SELECT invoice_no, line_no, 'PKG-003', sut_code, version_id, 'Inpatient', treatment_type
              FROM ln
             WHERE inpatient_only = 'Y' AND treatment_type <> 'Inpatient'
        )
        -- Only rules active in the invoice's version, with that version's severity.
        SELECT v_run_id, r.invoice_no, r.line_no, r.rule_id, a.severity, r.sut_code, r.expected_value, r.actual_value
          FROM candidates r
          JOIN sut_rule_activation a ON a.version_id = r.version_id AND a.rule_id = r.rule_id;

        v_findings := SQL%ROWCOUNT;

        UPDATE precheck_run
           SET finished_at = SYSTIMESTAMP, finding_count = v_findings
         WHERE run_id = v_run_id;

        p_run_id := v_run_id;
        OPEN p_findings FOR
            SELECT invoice_no, line_no, rule_id, severity, sut_code, expected_value, actual_value
              FROM precheck_finding
             WHERE run_id = v_run_id
             ORDER BY invoice_no, line_no NULLS FIRST, rule_id;
    END run_batch;
END precheck_pkg;
/
