-- Least privilege for the application role: it can stage a batch and execute the package, nothing else.
-- It cannot read reference data or audit tables directly and cannot change any of them.
-- ${APP_USER} is replaced by the installer with a validated identifier.
GRANT SELECT, INSERT ON stg_invoice TO ${APP_USER}
/
GRANT SELECT, INSERT ON stg_line TO ${APP_USER}
/
GRANT SELECT, INSERT ON stg_diagnosis TO ${APP_USER}
/
GRANT SELECT, INSERT ON stg_doctor TO ${APP_USER}
/
GRANT SELECT, INSERT ON stg_prior TO ${APP_USER}
/
GRANT EXECUTE ON precheck_pkg TO ${APP_USER}
/
