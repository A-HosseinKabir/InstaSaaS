-- Consolidated SQL Server schema (phases 1-8, all patches applied).
-- NOTE: the EF Core model (AppDbContext) is the source of truth. Prefer generating the real migration with
--   dotnet ef migrations add InitialSchema -p src/InstaSaaS.Api
-- and use this file as a readable reference. It was hand-written and NOT executed against SQL Server.
CREATE TABLE users (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    phone NVARCHAR(20) NOT NULL UNIQUE,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE plans (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    name NVARCHAR(100) NOT NULL,
    price_rial BIGINT NOT NULL,
    duration_days INT NOT NULL,
    max_instagram_accounts INT NOT NULL,
    max_automations INT NOT NULL,
    is_active BIT NOT NULL DEFAULT 1
);

CREATE TABLE tenants (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    name NVARCHAR(60) NULL,
    owner_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    plan_id UNIQUEIDENTIFIER NULL REFERENCES plans(id) ON DELETE SET NULL,
    subscription_status INT NOT NULL DEFAULT 0,   -- 0 Trial, 1 Active, 2 Expired, 3 Cancelled
    expire_date DATETIME2 NULL
);

-- Per-tenant roles (RBAC): 1 Owner, 2 Admin, 3 Viewer
CREATE TABLE tenant_members (
    tenant_id UNIQUEIDENTIFIER NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    role INT NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (tenant_id, user_id)
);

CREATE TABLE tenant_invites (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    tenant_id UNIQUEIDENTIFIER NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    phone NVARCHAR(20) NOT NULL,
    role INT NOT NULL,
    expires_at DATETIME2 NOT NULL,
    accepted_at DATETIME2 NULL
);
CREATE INDEX ix_invites_tenant_phone ON tenant_invites(tenant_id, phone);
CREATE INDEX ix_invites_phone ON tenant_invites(phone);

CREATE TABLE otp_codes (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    phone NVARCHAR(20) NOT NULL,
    code_hash NVARCHAR(200) NOT NULL,
    expires_at DATETIME2 NOT NULL,
    attempts INT NOT NULL DEFAULT 0,
    used BIT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_otp_phone_created ON otp_codes(phone, created_at);

CREATE TABLE invoices (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    tenant_id UNIQUEIDENTIFIER NOT NULL REFERENCES tenants(id),
    plan_id UNIQUEIDENTIFIER NOT NULL REFERENCES plans(id),
    amount_rial BIGINT NOT NULL,
    status INT NOT NULL DEFAULT 0,                -- 0 Pending, 1 Paid, 2 Failed
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_invoices_tenant ON invoices(tenant_id);

CREATE TABLE transactions (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    invoice_id UNIQUEIDENTIFIER NOT NULL REFERENCES invoices(id),
    gateway NVARCHAR(50) NOT NULL,
    authority NVARCHAR(100) NULL,
    ref_id NVARCHAR(100) NULL,
    success BIT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_transactions_authority ON transactions(authority);

-- No Instagram credentials are stored: BoxAPI holds the access, we keep only its account_id.
CREATE TABLE instagram_accounts (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    tenant_id UNIQUEIDENTIFIER NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    boxapi_account_id UNIQUEIDENTIFIER NOT NULL,
    instagram_user_id NVARCHAR(50) NOT NULL,
    username NVARCHAR(100) NOT NULL,
    is_active BIT NOT NULL DEFAULT 1,
    expires_at DATETIME2 NULL,
    CONSTRAINT uq_ig_tenant_username UNIQUE (tenant_id, username),
    CONSTRAINT uq_ig_box_account UNIQUE (boxapi_account_id)
);

CREATE TABLE automations (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    account_id UNIQUEIDENTIFIER NOT NULL REFERENCES instagram_accounts(id) ON DELETE CASCADE,
    trigger_type INT NOT NULL,                    -- 1 comment, 2 dm
    keywords NVARCHAR(2500) NOT NULL,
    match_type INT NOT NULL,                      -- 1 exact, 2 contains
    priority INT NOT NULL DEFAULT 0,
    follow_up_after_hours INT NULL,
    follow_up_text NVARCHAR(1000) NULL,
    is_active BIT NOT NULL DEFAULT 1
);
CREATE INDEX ix_auto_lookup ON automations(account_id, trigger_type, is_active);

CREATE TABLE responses (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    automation_id UNIQUEIDENTIFIER NOT NULL REFERENCES automations(id) ON DELETE CASCADE,
    message_type INT NOT NULL,                    -- 1 text, 2 image, 3 button
    content NVARCHAR(MAX) NOT NULL,
    require_follow BIT NOT NULL DEFAULT 0,
    sort_order INT NOT NULL DEFAULT 0
);

CREATE TABLE pending_follow_ups (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    account_id UNIQUEIDENTIFIER NOT NULL REFERENCES instagram_accounts(id) ON DELETE CASCADE,
    automation_id UNIQUEIDENTIFIER NOT NULL,
    ig_user_id NVARCHAR(100) NOT NULL,
    sent_at DATETIME2 NOT NULL,
    due_at DATETIME2 NOT NULL,
    replied BIT NOT NULL DEFAULT 0,
    follow_up_sent BIT NOT NULL DEFAULT 0,
    attempts INT NOT NULL DEFAULT 0
);
CREATE INDEX ix_followup_due ON pending_follow_ups(follow_up_sent, replied, due_at);

CREATE TABLE logs (
    id BIGINT IDENTITY PRIMARY KEY,
    account_id UNIQUEIDENTIFIER NOT NULL,
    event_type NVARCHAR(50) NOT NULL,
    status NVARCHAR(30) NOT NULL,
    error_message NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_logs_account_created ON logs(account_id, created_at);

CREATE TABLE processed_events (
    id NVARCHAR(200) PRIMARY KEY,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_processed_created ON processed_events(created_at);

CREATE TABLE follow_checks (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    account_id UNIQUEIDENTIFIER NOT NULL,
    automation_id UNIQUEIDENTIFIER NOT NULL,
    sender_ig_id NVARCHAR(50) NOT NULL,
    sender_username NVARCHAR(100) NOT NULL,
    event_type INT NOT NULL,
    comment_id NVARCHAR(100) NULL,
    text NVARCHAR(2200) NOT NULL,
    resume_from_sort INT NOT NULL,
    first_sent BIT NOT NULL,
    done BIT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_followchecks ON follow_checks(account_id, sender_ig_id, done, created_at);

CREATE TABLE connect_sessions (
    id UNIQUEIDENTIFIER PRIMARY KEY,
    tenant_id UNIQUEIDENTIFIER NOT NULL,
    user_id UNIQUEIDENTIFIER NOT NULL,
    completed BIT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Sample plans: EDIT names/prices/limits (placeholders). Prices are in RIALS.
INSERT INTO plans (id, name, price_rial, duration_days, max_instagram_accounts, max_automations, is_active) VALUES
(NEWID(), N'پایه',    3000000, 30, 1, 10, 1),
(NEWID(), N'حرفه‌ای', 8000000, 30, 3, 50, 1);
