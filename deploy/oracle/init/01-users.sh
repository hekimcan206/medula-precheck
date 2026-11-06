#!/bin/bash
# Runs once, on the first start of the gvenzl/oracle-free container, as the oracle OS user.
# Creates the schema owner (DDL, used only by the installer) and the application role (stage + execute only).
set -euo pipefail

: "${OWNER_PASSWORD:?OWNER_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

sqlplus -s / as sysdba <<SQL
WHENEVER SQLERROR EXIT SQL.SQLCODE
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER precheck_owner IDENTIFIED BY "${OWNER_PASSWORD}" DEFAULT TABLESPACE users QUOTA 512M ON users;
GRANT CREATE SESSION, CREATE TABLE, CREATE PROCEDURE, CREATE SEQUENCE TO precheck_owner;

CREATE USER precheck_app IDENTIFIED BY "${APP_PASSWORD}";
GRANT CREATE SESSION TO precheck_app;
EXIT
SQL
