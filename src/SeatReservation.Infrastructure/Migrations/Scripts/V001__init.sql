CREATE TABLE IF NOT EXISTS schema_migrations (
    version     text PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE shows (
    id                  uuid        PRIMARY KEY,
    name                text        NOT NULL CHECK (char_length(name) BETWEEN 1 AND 200),
    price_paise         bigint      NOT NULL CHECK (price_paise >= 0),
    per_user_limit      int         NOT NULL DEFAULT 4 CHECK (per_user_limit BETWEEN 1 AND 100),
    total_seats         int         NOT NULL CHECK (total_seats > 0),
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE reservations (
    id               uuid        PRIMARY KEY,
    show_id          uuid        NOT NULL REFERENCES shows(id),
    user_id          text        NOT NULL,
    idempotency_key  text        NOT NULL CHECK (char_length(idempotency_key) BETWEEN 1 AND 128),
    request_hash     bytea       NOT NULL,                -- SHA-256 (32 bytes)
    seats            text[]      NOT NULL,                -- sorted labels
    amount_paise     bigint      NOT NULL CHECK (amount_paise >= 0),
    status           text        NOT NULL CHECK (status IN ('confirmed','cancelled')),
    created_at       timestamptz NOT NULL DEFAULT now(),
    cancelled_at     timestamptz NULL,
    CONSTRAINT uq_reservations_user_key UNIQUE (user_id, idempotency_key),
    CONSTRAINT ck_cancelled_at CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL))
);
CREATE INDEX ix_reservations_show_user ON reservations (show_id, user_id);

CREATE TABLE seats (
    show_id         uuid        NOT NULL REFERENCES shows(id),
    label           text        NOT NULL,
    ordinal         int         NOT NULL,
    status          text        NOT NULL DEFAULT 'available'
                                CHECK (status IN ('available','held','confirmed')),
    user_id         text        NULL,
    reservation_id  uuid        NULL REFERENCES reservations(id),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_seats PRIMARY KEY (show_id, label),
    CONSTRAINT ck_seat_owner CHECK (
        (status = 'available' AND user_id IS NULL AND reservation_id IS NULL) OR
        (status <> 'available' AND user_id IS NOT NULL AND reservation_id IS NOT NULL))
);
CREATE INDEX ix_seats_show_user   ON seats (show_id, user_id) WHERE user_id IS NOT NULL;
CREATE INDEX ix_seats_reservation ON seats (reservation_id)  WHERE reservation_id IS NOT NULL;
CREATE UNIQUE INDEX ux_seats_show_ordinal ON seats (show_id, ordinal);
CREATE INDEX ix_shows_created ON shows (created_at DESC);          -- recent-shows gauge (D-086)
