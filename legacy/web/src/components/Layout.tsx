import { useState, type ReactNode } from 'react'
import { NavLink, useLocation } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useAuth } from '../lib/auth'
import { SyncBar } from './SyncBar'

export const NAV: Array<[string, string, string]> = [
  ['/', 'Home', '⌂'],
  ['/projects', 'Projects', '▦'],
  ['/dpr', 'Daily DPR', '✎'],
  ['/activities', 'Activities', '☰'],
  ['/schedule', 'Schedule', '▤'],
  ['/manpower', 'Manpower', '👷'],
  ['/equipment', 'Equipment', '🏗'],
  ['/materials', 'Materials', '📦'],
  ['/safety-qa', 'Safety & QA/QC', '⛑'],
  ['/documents', 'Documents', '📁'],
  ['/dashboards', 'Dashboards', '📈'],
  ['/approvals', 'Approvals', '✔'],
  ['/settings', 'Settings', '⚙'],
]
const BOTTOM = ['/', '/dpr', '/approvals', '/dashboards']

function NavItem({ to, label, icon, onClick }: { to: string; label: string; icon: string; onClick?: () => void }) {
  return (
    <NavLink to={to} end={to === '/'} onClick={onClick}
      className={({ isActive }) => `flex items-center gap-3 rounded-lg px-3 min-h-11 font-semibold ${isActive ? 'bg-brand-700 text-white' : 'text-slate-700 hover:bg-slate-200 dark:text-slate-200 dark:hover:bg-slate-800'}`}>
      <span aria-hidden className="w-6 text-center">{icon}</span>{label}
    </NavLink>
  )
}

export function Layout({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false)
  const { project } = useProject()
  const { profile, signOut } = useAuth()
  const loc = useLocation()
  return (
    <div className="min-h-dvh lg:grid lg:grid-cols-[250px_1fr]">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:bg-white focus:p-2">Skip to content</a>
      {/* Sidebar (desktop) / drawer (mobile) */}
      <aside className={`${open ? 'fixed inset-0 z-40 flex' : 'hidden'} lg:sticky lg:top-0 lg:flex lg:h-dvh`} aria-label="Main navigation">
        <div className="flex w-72 lg:w-full flex-col gap-1 overflow-y-auto bg-slate-50 dark:bg-slate-900 border-r border-slate-200 dark:border-slate-800 p-3">
          <div className="flex items-center gap-2 px-2 pb-3">
            <img src="/icon.svg" alt="" className="h-9 w-9" />
            <div>
              <div className="font-extrabold leading-tight">CONSTRUCT DPR</div>
              <div className="text-xs text-slate-500 truncate max-w-44">{project ? `${project.code} · ${project.name}` : 'No project selected'}</div>
            </div>
          </div>
          <nav className="flex flex-col gap-1">
            {NAV.map(([to, label, icon]) => <NavItem key={to} to={to} label={label} icon={icon} onClick={() => setOpen(false)} />)}
          </nav>
          <div className="mt-auto pt-4 text-sm text-slate-600 dark:text-slate-400 px-2">
            <div className="truncate">{profile?.full_name}</div>
            <button className="btn btn-ghost px-0" onClick={() => void signOut()}>Sign out</button>
          </div>
        </div>
        {open && <button aria-label="Close menu" className="flex-1 bg-black/40 lg:hidden" onClick={() => setOpen(false)} />}
      </aside>

      <div className="flex min-h-dvh flex-col">
        <header className="sticky top-0 z-30 flex items-center gap-2 bg-brand-700 px-3 py-2 text-white lg:hidden">
          <button className="btn px-2 text-white" onClick={() => setOpen(true)} aria-label="Open menu">☰</button>
          <div className="min-w-0 flex-1">
            <div className="font-extrabold leading-tight">CONSTRUCT DPR</div>
            <div className="truncate text-xs opacity-80">{project ? `${project.code} · ${project.name}` : 'Select a project'}</div>
          </div>
        </header>
        <SyncBar />
        <main id="main" className="flex-1 w-full max-w-6xl mx-auto px-3 py-4 pb-24 lg:pb-8 sm:px-5">{children}</main>
        {/* Bottom navigation (phones) */}
        <nav className="fixed bottom-0 inset-x-0 z-30 grid grid-cols-5 border-t border-slate-200 bg-white dark:bg-slate-900 dark:border-slate-800 lg:hidden pb-[env(safe-area-inset-bottom)]" aria-label="Quick navigation">
          {BOTTOM.map((to) => {
            const [, label, icon] = NAV.find((n) => n[0] === to)!
            const active = to === '/' ? loc.pathname === '/' : loc.pathname.startsWith(to)
            return (
              <NavLink key={to} to={to} className={`flex flex-col items-center justify-center min-h-14 text-xs font-semibold ${active ? 'text-brand-700 dark:text-brand-100' : 'text-slate-500'}`}>
                <span aria-hidden className="text-lg">{icon}</span>{label}
              </NavLink>
            )
          })}
          <button onClick={() => setOpen(true)} className="flex flex-col items-center justify-center min-h-14 text-xs font-semibold text-slate-500">
            <span aria-hidden className="text-lg">⋯</span>More
          </button>
        </nav>
      </div>
    </div>
  )
}
