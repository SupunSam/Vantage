-- Vantage: local database bootstrap (the database keeps its original name, RDDashboard).
-- Creates the RDDashboard database, an application login (rd_app),
-- and the hrms.EmployeeProfile table that the SQL-level HRMS sync will fill in real environments.
-- Safe to run more than once.

IF DB_ID(N'RDDashboard') IS NULL
    CREATE DATABASE RDDashboard;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'rd_app')
    CREATE LOGIN rd_app WITH PASSWORD = N'$(APP_PASSWORD)', CHECK_POLICY = ON;
GO

USE RDDashboard;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'rd_app')
BEGIN
    CREATE USER rd_app FOR LOGIN rd_app;
    -- db_owner so EF Core migrations can create the application schema locally.
    ALTER ROLE db_owner ADD MEMBER rd_app;
END
GO

IF SCHEMA_ID(N'hrms') IS NULL
    EXEC (N'CREATE SCHEMA hrms');
GO

-- Mirror of the HRMS source. In real environments this is populated by the SQL-level sync;
-- the portal's monthly job reads it and updates user profiles and statuses.
IF OBJECT_ID(N'hrms.EmployeeProfile', N'U') IS NULL
BEGIN
    CREATE TABLE hrms.EmployeeProfile (
        EmployeeId        NVARCHAR(20)  NOT NULL PRIMARY KEY,
        Email             NVARCHAR(256) NOT NULL,
        FirstName         NVARCHAR(100) NOT NULL,
        LastName          NVARCHAR(100) NOT NULL,
        DisplayName       NVARCHAR(200) NULL,
        Department        NVARCHAR(100) NULL,
        Division          NVARCHAR(100) NULL,
        JobTitle          NVARCHAR(150) NULL,
        JobGrade          NVARCHAR(20)  NULL,
        ManagerEmail      NVARCHAR(256) NULL,
        Location          NVARCHAR(100) NULL,
        EmploymentStatus  NVARCHAR(20)  NOT NULL,   -- Active | Inactive
        HireDate          DATE          NOT NULL,
        ExitDate          DATE          NULL,
        LastSyncedAtUtc   DATETIME2(0)  NOT NULL CONSTRAINT DF_EmployeeProfile_LastSynced DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_EmployeeProfile_Email UNIQUE (Email),
        CONSTRAINT CK_EmployeeProfile_Status CHECK (EmploymentStatus IN (N'Active', N'Inactive'))
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM hrms.EmployeeProfile)
BEGIN
    INSERT INTO hrms.EmployeeProfile
        (EmployeeId, Email, FirstName, LastName, DisplayName, Department, Division, JobTitle, JobGrade, ManagerEmail, Location, EmploymentStatus, HireDate, ExitDate)
    VALUES
        (N'E1001', N'nimal.perera@rrd.com',      N'Nimal',    N'Perera',      N'Nimal Perera',      N'Business Process Improvement', N'Analytics',  N'Head of BPI',            N'M3', NULL,                         N'Colombo',   N'Active',   '2015-03-02', NULL),
        (N'E1002', N'anjali.fernando@rrd.com',   N'Anjali',   N'Fernando',    N'Anjali Fernando',   N'Business Process Improvement', N'Analytics',  N'Senior Data Scientist',  N'L4', N'nimal.perera@rrd.com',      N'Colombo',   N'Active',   '2018-06-11', NULL),
        (N'E1003', N'kasun.silva@rrd.com',       N'Kasun',    N'Silva',       N'Kasun Silva',       N'Business Process Improvement', N'Analytics',  N'Data Scientist',         N'L3', N'nimal.perera@rrd.com',      N'Colombo',   N'Active',   '2020-01-20', NULL),
        (N'E1004', N'tharushi.jayawardena@rrd.com', N'Tharushi', N'Jayawardena', N'Tharushi Jayawardena', N'Business Process Improvement', N'Analytics', N'Data Analyst',       N'L2', N'anjali.fernando@rrd.com',   N'Colombo',   N'Active',   '2023-09-04', NULL),
        (N'E1005', N'ruwan.dias@rrd.com',        N'Ruwan',    N'Dias',        N'Ruwan Dias',        N'Application Services Desk',    N'IT',         N'Help Desk Lead',         N'L4', N'sarah.mitchell@rrd.com',    N'Colombo',   N'Active',   '2016-11-14', NULL),
        (N'E1006', N'dilini.rathnayake@rrd.com', N'Dilini',   N'Rathnayake',  N'Dilini Rathnayake', N'Application Services Desk',    N'IT',         N'Help Desk Analyst',      N'L2', N'ruwan.dias@rrd.com',        N'Colombo',   N'Active',   '2022-02-07', NULL),
        (N'E1007', N'sarah.mitchell@rrd.com',    N'Sarah',    N'Mitchell',    N'Sarah Mitchell',    N'IT Operations',                N'IT',         N'IT Operations Manager',  N'M2', NULL,                         N'Chicago',   N'Active',   '2012-05-21', NULL),
        (N'E1008', N'james.oconnor@rrd.com',     N'James',    N'O''Connor',   N'James O''Connor',   N'Finance',                      N'Corporate',  N'Finance Director',       N'M3', NULL,                         N'Chicago',   N'Active',   '2010-08-30', NULL),
        (N'E1009', N'priya.nair@rrd.com',        N'Priya',    N'Nair',        N'Priya Nair',        N'Finance',                      N'Corporate',  N'Financial Analyst',      N'L3', N'james.oconnor@rrd.com',     N'Chennai',   N'Active',   '2019-04-15', NULL),
        (N'E1010', N'arjun.menon@rrd.com',       N'Arjun',    N'Menon',       N'Arjun Menon',       N'Finance',                      N'Corporate',  N'Budget Analyst',         N'L2', N'james.oconnor@rrd.com',     N'Chennai',   N'Active',   '2024-07-01', NULL),
        (N'E1011', N'maria.gonzalez@rrd.com',    N'Maria',    N'Gonzalez',    N'Maria Gonzalez',    N'Operations',                   N'Delivery',   N'Operations Director',    N'M3', NULL,                         N'Monterrey', N'Active',   '2014-02-10', NULL),
        (N'E1012', N'luis.ramirez@rrd.com',      N'Luis',     N'Ramirez',     N'Luis Ramirez',      N'Operations',                   N'Delivery',   N'Team Lead',              N'L4', N'maria.gonzalez@rrd.com',    N'Monterrey', N'Active',   '2017-10-23', NULL),
        (N'E1013', N'chen.wei@rrd.com',          N'Wei',      N'Chen',        N'Wei Chen',          N'Operations',                   N'Delivery',   N'Process Associate',      N'L1', N'luis.ramirez@rrd.com',      N'Monterrey', N'Active',   '2026-09-14', NULL),
        (N'E1014', N'emily.carter@rrd.com',      N'Emily',    N'Carter',      N'Emily Carter',      N'Client Services',              N'Sales',      N'Account Director',       N'M2', NULL,                         N'Chicago',   N'Active',   '2013-01-07', NULL),
        (N'E1015', N'daniel.brooks@rrd.com',     N'Daniel',   N'Brooks',      N'Daniel Brooks',     N'Client Services',              N'Sales',      N'Account Manager',        N'L3', N'emily.carter@rrd.com',      N'Chicago',   N'Active',   '2021-03-29', NULL),
        (N'E1016', N'hasini.wickramasinghe@rrd.com', N'Hasini', N'Wickramasinghe', N'Hasini Wickramasinghe', N'Human Resources',      N'Corporate',  N'HR Business Partner',    N'L4', N'james.oconnor@rrd.com',     N'Colombo',   N'Active',   '2018-12-03', NULL),
        (N'E1017', N'sanjay.kumar@rrd.com',      N'Sanjay',   N'Kumar',       N'Sanjay Kumar',      N'Operations',                   N'Delivery',   N'Process Associate',      N'L1', N'luis.ramirez@rrd.com',      N'Chennai',   N'Active',   '2026-09-21', NULL),
        -- Leavers: exercise the inactivation and owner-handover rules
        (N'E1018', N'kavinda.bandara@rrd.com',   N'Kavinda',  N'Bandara',     N'Kavinda Bandara',   N'Business Process Improvement', N'Analytics',  N'Data Scientist',         N'L3', N'nimal.perera@rrd.com',      N'Colombo',   N'Inactive', '2019-05-06', '2026-08-29'),
        (N'E1019', N'olivia.hughes@rrd.com',     N'Olivia',   N'Hughes',      N'Olivia Hughes',     N'Finance',                      N'Corporate',  N'Senior Accountant',      N'L4', N'james.oconnor@rrd.com',     N'Chicago',   N'Inactive', '2016-09-12', '2026-07-31'),
        (N'E1020', N'tom.baker@rrd.com',         N'Tom',      N'Baker',       N'Tom Baker',         N'Client Services',              N'Sales',      N'Account Manager',        N'L3', N'emily.carter@rrd.com',      N'Chicago',   N'Inactive', '2020-11-02', '2026-09-30');
END
GO

DECLARE @rows INT = (SELECT COUNT(*) FROM hrms.EmployeeProfile);
PRINT N'RDDashboard ready: ' + CAST(@rows AS NVARCHAR(10)) + N' HRMS rows.';
GO
