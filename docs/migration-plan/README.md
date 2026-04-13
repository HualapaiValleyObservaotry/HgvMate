# HgvMate Migration Plan

This folder contains the phased migration plan for moving HgvMate into official Azure DevOps repositories and pipelines, introducing service-level credentials, and enabling Entra authentication for MCP and REST endpoints.

## Scope

Included in this migration track:
- Official Azure DevOps repository and pipeline migration
- Service-level generic PAT strategy for system DevOps operations
- Entra JWT authentication for MCP and REST endpoints
- Production hardening prerequisites and rollout validation

Excluded from initial migration:
- Full user-delegated identity implementation for user-attributed write actions (planned follow-on)
- Full RBAC productization beyond baseline endpoint policies

## Phase Overview

1. Phase 0 - Decision freeze and operating model
2. Phase 1 - Azure DevOps repository and pipeline migration foundation
3. Phase 2 - Generic PAT architecture for service-level DevOps operations
4. Phase 3 - Entra auth for MCP and REST endpoints
5. Phase 4 - Mixed service and delegated-user access model (future-proofing)
6. Phase 5 - Security and compliance hardening
7. Phase 6 - Verification and production cutover

## Current Focus

Start here:
- phase-1-checklist.md
- phase-1-runbook.md
