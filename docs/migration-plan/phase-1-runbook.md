# Phase 1 Runbook - Move to Official Azure DevOps Repo and Pipeline

## Purpose

Operational instructions for executing Phase 1 with clear sequencing, ownership, and verification.

## Inputs

Required before start:
- Access to official Azure DevOps organization and project
- Permission to create repos, pipelines, service connections, and variable groups
- Current repo URL and admin rights for export and import
- Container registry target and credentials

## Roles

- Migration Lead: Executes repository move and pipeline bootstrap
- Platform Engineer: Establishes service connections and registry integration
- Security Reviewer: Reviews branch policy and secret handling
- App Maintainer: Verifies build and test parity and runtime behavior

## Sequence

### Step 1 - Prepare official target
1. Create official project and repository for HgvMate.
2. Confirm naming conventions and branch strategy.
3. Configure baseline permissions for maintainers and reviewers.

Deliverable:
- Official repo URL and access model documented.

### Step 2 - Import source history
1. Mirror or import from current repository into official repository.
2. Validate branches, tags, and commit history integrity.
3. Set main as default branch.

Deliverable:
- History-preserved official repository with main active.

### Step 3 - Apply branch and PR governance
1. Configure PR-required workflow for main.
2. Require successful validation build before merge.
3. Set minimum reviewers and disable direct pushes.

Deliverable:
- Protected main branch with enforced policy.

### Step 4 - Bootstrap pipeline
1. Add pipeline YAML with these jobs:
- dotnet restore and build
- dotnet test
- docker build
- docker push to official registry
2. Configure PR and main triggers.
3. Configure artifact retention and naming.

Deliverable:
- First green pipeline run on main and PR validation path.

### Step 5 - Configure secrets and service connections
1. Add secure variables for service PAT and registry auth.
2. Add service connection for container registry.
3. Add placeholder Entra settings for upcoming auth phases.
4. Verify no secrets appear in logs.

Deliverable:
- Secret-backed pipeline ready for non-interactive execution.

### Step 6 - Parity validation
1. Compare build outcomes versus current baseline:
- Test pass count
- Container build success
- Image tag conventions
2. Run container smoke validation (health endpoint and startup readiness).

Deliverable:
- Signed parity report with pass and fail evidence.

### Step 7 - Handover and close Phase 1
1. Publish runbook links and ownership map.
2. Record known gaps carried to Phase 2 and 3.
3. Obtain sign-off from migration lead, platform, and security.

Deliverable:
- Phase 1 complete and approved for Phase 2 kickoff.

## Verification Commands (for execution team)

- dotnet build
- dotnet test
- docker build -t hgvmate-phase1-verify .
- docker run --rm -p 5000:5000 -e HGVMATE_TRANSPORT=sse hgvmate-phase1-verify

## Evidence to Capture

- Pipeline run URL (PR and main)
- Branch policy screenshot or export
- Secret variable group redaction proof
- Test summary and container publish logs
- Phase 1 sign-off record

## Out of Scope for Phase 1

- Implementing generic PAT precedence in app runtime (Phase 2)
- Implementing Entra endpoint auth in app runtime (Phase 3)
- Delegated user identity flows for user-attributed write actions (Phase 4 and later)
