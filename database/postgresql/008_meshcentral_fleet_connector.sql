BEGIN;

CREATE TABLE IF NOT EXISTS meshcentral_connectors (
  connector_id uuid PRIMARY KEY,
  name text NOT NULL,
  base_uri text NOT NULL,
  username text NOT NULL,
  authentication_mode text NOT NULL CHECK (authentication_mode IN ('password','loginkey')),
  protected_credential text NOT NULL,
  meshctrl_path text NULL,
  enabled boolean NOT NULL DEFAULT true,
  sync_interval_minutes integer NOT NULL CHECK (sync_interval_minutes BETWEEN 1 AND 1440),
  created_at_utc timestamptz NOT NULL,
  updated_at_utc timestamptz NOT NULL,
  last_connection_test_at_utc timestamptz NULL,
  last_connection_test_succeeded boolean NULL,
  last_connection_test_message text NULL,
  last_inventory_sync_at_utc timestamptz NULL,
  server_version text NULL
);

CREATE TABLE IF NOT EXISTS meshcentral_inventory_devices (
  node_id text PRIMARY KEY,
  connector_id uuid NOT NULL REFERENCES meshcentral_connectors(connector_id) ON DELETE CASCADE,
  name text NOT NULL,
  hostname text NULL,
  domain_name text NULL,
  group_name text NULL,
  online boolean NOT NULL,
  observed_at_utc timestamptz NOT NULL,
  matched_agent_id uuid NULL,
  match_status text NOT NULL CHECK (match_status IN ('matched','missing','ambiguous')),
  match_evidence text NULL,
  agent_version text NULL
);
CREATE INDEX IF NOT EXISTS ix_meshcentral_inventory_connector ON meshcentral_inventory_devices(connector_id);
CREATE INDEX IF NOT EXISTS ix_meshcentral_inventory_match_status ON meshcentral_inventory_devices(match_status);

CREATE TABLE IF NOT EXISTS meshcentral_deployments (
  deployment_id uuid PRIMARY KEY,
  connector_id uuid NOT NULL REFERENCES meshcentral_connectors(connector_id) ON DELETE CASCADE,
  node_id text NOT NULL,
  device_name text NOT NULL,
  agent_id uuid NULL,
  status text NOT NULL CHECK (status IN ('queued','dispatched','installing','succeeded','failed')),
  requested_at_utc timestamptz NOT NULL,
  updated_at_utc timestamptz NOT NULL,
  detail text NULL,
  command_id text NULL
);
CREATE INDEX IF NOT EXISTS ix_meshcentral_deployments_connector_status ON meshcentral_deployments(connector_id, status);
CREATE INDEX IF NOT EXISTS ix_meshcentral_deployments_node ON meshcentral_deployments(node_id, requested_at_utc DESC);

COMMIT;
