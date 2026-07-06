-- ============================================================================
-- Qwiik Invoicing — SQL Server schema
--
-- This script matches the EF Core model exactly. Two ways to create the schema:
--   1. Preferred: dotnet ef migrations add InitialCreate && dotnet ef database update
--      (see README.md — migrations were not committed because the solution was
--       authored in an offline environment; generating them takes one command)
--   2. Run this script directly against an empty database.
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = 'QwiikInvoicing')
    CREATE DATABASE QwiikInvoicing;
GO

USE QwiikInvoicing;
GO

IF OBJECT_ID('dbo.InvoiceLineItems', 'U') IS NOT NULL DROP TABLE dbo.InvoiceLineItems;
IF OBJECT_ID('dbo.Invoices', 'U') IS NOT NULL DROP TABLE dbo.Invoices;
GO

CREATE TABLE dbo.Invoices
(
    Id                 UNIQUEIDENTIFIER NOT NULL,
    TenantId           UNIQUEIDENTIFIER NOT NULL,
    InvoiceNumber      NVARCHAR(30)     NOT NULL,
    CustomerName       NVARCHAR(200)    NOT NULL,
    CustomerEmail      NVARCHAR(320)    NULL,
    Currency           NVARCHAR(3)      NOT NULL,
    IssueDate          DATE             NOT NULL,
    DueDate            DATE             NOT NULL,
    TaxRate            DECIMAL(5, 2)    NOT NULL,
    Notes              NVARCHAR(2000)   NULL,
    Status             NVARCHAR(20)     NOT NULL,
    Subtotal           DECIMAL(18, 2)   NOT NULL,
    TaxAmount          DECIMAL(18, 2)   NOT NULL,
    Total              DECIMAL(18, 2)   NOT NULL,
    CreatedAtUtc       DATETIME2        NOT NULL,
    UpdatedAtUtc       DATETIME2        NOT NULL,
    ConcurrencyToken   UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT PK_Invoices PRIMARY KEY (Id)
);
GO

CREATE TABLE dbo.InvoiceLineItems
(
    Id          UNIQUEIDENTIFIER NOT NULL,
    InvoiceId   UNIQUEIDENTIFIER NOT NULL,
    Description NVARCHAR(500)    NOT NULL,
    Quantity    DECIMAL(18, 3)   NOT NULL,
    UnitPrice   DECIMAL(18, 2)   NOT NULL,
    LineTotal   DECIMAL(18, 2)   NOT NULL,
    CONSTRAINT PK_InvoiceLineItems PRIMARY KEY (Id),
    CONSTRAINT FK_InvoiceLineItems_Invoices_InvoiceId
        FOREIGN KEY (InvoiceId) REFERENCES dbo.Invoices (Id) ON DELETE CASCADE
);
GO

-- Every index leads with TenantId: all application queries are tenant-scoped,
-- so this turns cross-tenant scans into per-tenant seeks.
CREATE UNIQUE INDEX IX_Invoices_TenantId_InvoiceNumber ON dbo.Invoices (TenantId, InvoiceNumber);
CREATE INDEX IX_Invoices_TenantId_Status       ON dbo.Invoices (TenantId, Status);
CREATE INDEX IX_Invoices_TenantId_IssueDate    ON dbo.Invoices (TenantId, IssueDate);
CREATE INDEX IX_Invoices_TenantId_DueDate      ON dbo.Invoices (TenantId, DueDate);
CREATE INDEX IX_Invoices_TenantId_CreatedAtUtc ON dbo.Invoices (TenantId, CreatedAtUtc);
CREATE INDEX IX_InvoiceLineItems_InvoiceId     ON dbo.InvoiceLineItems (InvoiceId);
GO
