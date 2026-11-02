/**
 * Feature switches, fixed at build time.
 *
 * ADMIN_PORTAL — administration and project-setup screens: creating projects,
 * Members & roles, project setup (areas, WBS, BOQ, calendar, registers),
 * editing activities and schedule logic, baselines, Drive folder management,
 * the connection code, integrations and the permission matrix.
 * Off for now (build with VITE_ADMIN_PORTAL=true to bring it all back).
 * Turning it off only hides screens; the database still enforces every
 * permission, so nothing becomes less secure.
 */
export const ADMIN_PORTAL = import.meta.env.VITE_ADMIN_PORTAL === 'true'
