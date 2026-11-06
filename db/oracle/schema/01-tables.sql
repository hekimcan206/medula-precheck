-- MEDULA pre-check: reference data, staging and audit tables. Run as the schema owner.
-- Every statement ends with a line containing only "/" (the installer splits on it).
-- Oracle 23ai IF NOT EXISTS keeps the script re-runnable without a migration framework.

-- ---------------------------------------------------------------------------------------------------
-- Reference data: one row set per SUT version, loaded from the same JSON catalog the .NET engine uses.
-- ---------------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sut_version (
    version_id            VARCHAR2(20) NOT NULL,
    effective_from        DATE         NOT NULL,
    effective_to          DATE,
    exam_repeat_days      NUMBER(4)    NOT NULL,
    max_invoice_lag_days  NUMBER(4)    NOT NULL,
    CONSTRAINT pk_sut_version PRIMARY KEY (version_id),
    CONSTRAINT ck_sut_version_range CHECK (effective_to IS NULL OR effective_to > effective_from)
)
/
CREATE TABLE IF NOT EXISTS sut_rule_activation (
    version_id  VARCHAR2(20) NOT NULL,
    rule_id     VARCHAR2(10) NOT NULL,
    severity    VARCHAR2(10) NOT NULL,
    CONSTRAINT pk_sut_rule_activation PRIMARY KEY (version_id, rule_id),
    CONSTRAINT fk_rule_activation_version FOREIGN KEY (version_id) REFERENCES sut_version (version_id),
    CONSTRAINT ck_rule_activation_severity CHECK (severity IN ('Info', 'Warning', 'Error'))
)
/
CREATE TABLE IF NOT EXISTS sut_procedure (
    version_id      VARCHAR2(20)  NOT NULL,
    code            VARCHAR2(10)  NOT NULL,
    kind            VARCHAR2(20)  NOT NULL,
    price           NUMBER(12, 2) NOT NULL,
    gender          VARCHAR2(6),
    min_age         NUMBER(3),
    max_age         NUMBER(3),
    max_per_day     NUMBER(4),
    freq_count      NUMBER(4),
    freq_days       NUMBER(5),
    inpatient_only  CHAR(1)       NOT NULL,
    CONSTRAINT pk_sut_procedure PRIMARY KEY (version_id, code),
    CONSTRAINT fk_procedure_version FOREIGN KEY (version_id) REFERENCES sut_version (version_id),
    CONSTRAINT ck_procedure_gender CHECK (gender IN ('Female', 'Male')),
    CONSTRAINT ck_procedure_inpatient CHECK (inpatient_only IN ('Y', 'N')),
    CONSTRAINT ck_procedure_frequency CHECK ((freq_count IS NULL AND freq_days IS NULL) OR (freq_count > 0 AND freq_days > 0))
)
/
CREATE TABLE IF NOT EXISTS sut_procedure_icd (
    version_id  VARCHAR2(20) NOT NULL,
    code        VARCHAR2(10) NOT NULL,
    icd_prefix  VARCHAR2(8)  NOT NULL,
    CONSTRAINT pk_sut_procedure_icd PRIMARY KEY (version_id, code, icd_prefix),
    CONSTRAINT fk_procedure_icd FOREIGN KEY (version_id, code) REFERENCES sut_procedure (version_id, code)
)
/
CREATE TABLE IF NOT EXISTS sut_procedure_branch (
    version_id   VARCHAR2(20) NOT NULL,
    code         VARCHAR2(10) NOT NULL,
    branch_code  VARCHAR2(8)  NOT NULL,
    CONSTRAINT pk_sut_procedure_branch PRIMARY KEY (version_id, code, branch_code),
    CONSTRAINT fk_procedure_branch FOREIGN KEY (version_id, code) REFERENCES sut_procedure (version_id, code)
)
/
CREATE TABLE IF NOT EXISTS sut_package_content (
    version_id     VARCHAR2(20) NOT NULL,
    package_code   VARCHAR2(10) NOT NULL,
    included_code  VARCHAR2(10) NOT NULL,
    CONSTRAINT pk_sut_package_content PRIMARY KEY (version_id, package_code, included_code),
    CONSTRAINT fk_package_content FOREIGN KEY (version_id, package_code) REFERENCES sut_procedure (version_id, code)
)
/
CREATE TABLE IF NOT EXISTS sut_incompatible_pair (
    version_id      VARCHAR2(20) NOT NULL,
    primary_code    VARCHAR2(10) NOT NULL,
    secondary_code  VARCHAR2(10) NOT NULL,
    CONSTRAINT pk_sut_incompatible_pair PRIMARY KEY (version_id, primary_code, secondary_code),
    CONSTRAINT fk_incompatible_version FOREIGN KEY (version_id) REFERENCES sut_version (version_id)
)
/
-- ---------------------------------------------------------------------------------------------------
-- Staging: session-private global temporary tables. A batch is array-inserted and checked in one
-- transaction; COMMIT empties them, so concurrent sessions never see each other's invoices and there
-- is no cleanup job. The app role may only insert/select here.
-- ---------------------------------------------------------------------------------------------------
CREATE GLOBAL TEMPORARY TABLE IF NOT EXISTS stg_invoice (
    invoice_no      VARCHAR2(30) NOT NULL,
    invoice_date    DATE         NOT NULL,
    treatment_type  VARCHAR2(12) NOT NULL,
    birth_date      DATE         NOT NULL,
    gender          VARCHAR2(6)  NOT NULL,
    admission_date  DATE         NOT NULL,
    discharge_date  DATE,
    CONSTRAINT pk_stg_invoice PRIMARY KEY (invoice_no)
) ON COMMIT DELETE ROWS
/
CREATE GLOBAL TEMPORARY TABLE IF NOT EXISTS stg_line (
    invoice_no     VARCHAR2(30)  NOT NULL,
    line_no        NUMBER(6)     NOT NULL,
    sut_code       VARCHAR2(10)  NOT NULL,
    service_date   DATE          NOT NULL,
    quantity       NUMBER(6)     NOT NULL,
    unit_price     NUMBER(12, 2) NOT NULL,
    doctor_reg_no  VARCHAR2(20)  NOT NULL,
    CONSTRAINT pk_stg_line PRIMARY KEY (invoice_no, line_no)
) ON COMMIT DELETE ROWS
/
CREATE GLOBAL TEMPORARY TABLE IF NOT EXISTS stg_diagnosis (
    invoice_no  VARCHAR2(30) NOT NULL,
    icd_code    VARCHAR2(10) NOT NULL,
    is_primary  CHAR(1)      NOT NULL
) ON COMMIT DELETE ROWS
/
CREATE GLOBAL TEMPORARY TABLE IF NOT EXISTS stg_doctor (
    invoice_no   VARCHAR2(30) NOT NULL,
    reg_no       VARCHAR2(20) NOT NULL,
    branch_code  VARCHAR2(8)  NOT NULL,
    CONSTRAINT pk_stg_doctor PRIMARY KEY (invoice_no, reg_no)
) ON COMMIT DELETE ROWS
/
CREATE GLOBAL TEMPORARY TABLE IF NOT EXISTS stg_prior (
    invoice_no    VARCHAR2(30) NOT NULL,
    sut_code      VARCHAR2(10) NOT NULL,
    service_date  DATE         NOT NULL,
    quantity      NUMBER(6)    NOT NULL,
    branch_code   VARCHAR2(8)  NOT NULL
) ON COMMIT DELETE ROWS
/
CREATE INDEX IF NOT EXISTS ix_stg_prior ON stg_prior (invoice_no, sut_code, service_date)
/
-- ---------------------------------------------------------------------------------------------------
-- Audit: every run and every finding is kept, with the database user that ran it.
-- ---------------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS precheck_run (
    run_id         NUMBER GENERATED ALWAYS AS IDENTITY,
    started_at     TIMESTAMP WITH TIME ZONE NOT NULL,
    finished_at    TIMESTAMP WITH TIME ZONE,
    invoice_count  NUMBER(9) NOT NULL,
    line_count     NUMBER(9) NOT NULL,
    finding_count  NUMBER(9),
    executed_by    VARCHAR2(128) NOT NULL,
    CONSTRAINT pk_precheck_run PRIMARY KEY (run_id)
)
/
CREATE TABLE IF NOT EXISTS precheck_finding (
    run_id          NUMBER        NOT NULL,
    invoice_no      VARCHAR2(30)  NOT NULL,
    line_no         NUMBER(6),
    rule_id         VARCHAR2(10)  NOT NULL,
    severity        VARCHAR2(10)  NOT NULL,
    sut_code        VARCHAR2(10),
    expected_value  VARCHAR2(400),
    actual_value    VARCHAR2(400),
    CONSTRAINT fk_finding_run FOREIGN KEY (run_id) REFERENCES precheck_run (run_id)
)
/
CREATE INDEX IF NOT EXISTS ix_precheck_finding_run ON precheck_finding (run_id, invoice_no)
/
