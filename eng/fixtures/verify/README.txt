Deterministic fixtures for Verify.ps1 self-check. They are data only and never participate in solution builds.

Run from repository root:
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/fixtures/verify/self-check.ps1

The self-check uses temporary copies, verifies the baseline manifest, and expects duplicate, disabled, order, and illegal-argument cases to fail. It never accesses providers, credentials, settings, OMP roots, bin/obj, or UI.
