# Phase 1 Checklist - Official Azure DevOps Migration Foundation

## Objective

Establish official source control and CI/CD foundation in Azure DevOps with parity to current build, test, and container behavior.

## Exit Criteria

All items below are complete and verified:
- Repository imported into official Azure DevOps project with history preserved
- Branch protections and PR policies enabled on main
- Pipeline runs build, tests, and container publish path successfully
- Secrets are stored in official secure pipeline variables or secret store
- Deployment artifact and image available in official registry path
- Operations runbook for repo and pipeline ownership published

## Checklist

### A. Repository and Governance
- [ ] Create official Azure DevOps project and repository namespace for HgvMate
- [ ] Import git history from current repository into official repo
- [ ] Set default branch to main
- [ ] Enable branch policies
- [ ] Require PR validation build
- [ ] Require reviewer minimum count
- [ ] Block direct pushes to main
- [ ] Enable status checks before merge
- [ ] Define code owners and reviewer groups

### B. Pipeline Foundation
- [ ] Create pipeline YAML in official repo (or migrate existing workflow logic)
- [ ] Add stages and jobs for restore and build
- [ ] Add stages and jobs for test (dotnet test)
- [ ] Add stages and jobs for container build (Dockerfile)
- [ ] Add stages and jobs for container publish to official registry
- [ ] Configure triggers for PR validation
- [ ] Configure triggers for main branch CI
- [ ] Configure optional release tag trigger

### C. Secrets and Configuration
- [ ] Create secure variables and service connections for AZURE_DEVOPS_PAT_SERVICE (service-level PAT)
- [ ] Create secure variables and service connections for registry credentials
- [ ] Create Entra auth placeholders (tenant, audience, client IDs for later phases)
- [ ] Remove plaintext secret usage from pipeline definitions
- [ ] Validate secrets are masked in logs

### D. Parity Validation
- [ ] Confirm dotnet build succeeds in official pipeline
- [ ] Confirm dotnet test succeeds in official pipeline
- [ ] Confirm Docker image builds successfully
- [ ] Confirm runtime image starts with expected environment wiring
- [ ] Compare outputs with current baseline (test counts, artifact naming, image tags)

### E. Operational Readiness
- [ ] Document ownership matrix for app team owner
- [ ] Document ownership matrix for platform and pipeline owner
- [ ] Document ownership matrix for security approver
- [ ] Publish rollback procedure for failed pipeline or bad image publish
- [ ] Publish Phase 1 completion report and sign-off

## Risks and Mitigations

- Risk: Pipeline drift from current behavior
Mitigation: Add explicit parity checklist and output comparison before cutover.

- Risk: Secret leakage in logs
Mitigation: Use secret variable groups and service connections only, and avoid verbose env echo.

- Risk: Build failures due to external dependency egress
Mitigation: Validate allowed endpoints and agent network policies early.
