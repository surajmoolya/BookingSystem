# Shortcuts for the common commands (lld §12, §13.4). Everything here also works without make; see README.
#   make up                                   API + Postgres in Docker on http://localhost:8080
#   make burst BASE_URL=http://localhost:8080 burst tool against a running service (ARGS="--scenario hot" to narrow)
#   make test-unit / make test-integration    test suites (integration needs Docker for Testcontainers)

BASE_URL ?=
ARGS ?=

.PHONY: help up down logs burst test test-unit test-integration

help:
	@echo "make up | down | logs | burst BASE_URL=<url> [ARGS=...] | test | test-unit | test-integration"

up:
	docker compose up -d --build db api

down:
	docker compose down

logs:
	docker compose logs -f api

burst:
ifeq ($(strip $(BASE_URL)),)
	$(error BASE_URL is required, e.g. make burst BASE_URL=http://localhost:8080)
endif
	./burst.sh $(BASE_URL) $(ARGS)

test: test-unit test-integration

test-unit:
	dotnet test tests/SeatReservation.UnitTests

test-integration:
	dotnet test tests/SeatReservation.IntegrationTests --filter "Category!=Load"
