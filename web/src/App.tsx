import { lazy, Suspense, useEffect, type ReactNode } from 'react'
import { Link, Route, Routes } from 'react-router-dom'
import { AuthProvider, useAuth } from './lib/auth'
import { ProjectProvider, useProject } from './lib/project'
import { useTheme } from './lib/hooks'
import { sync } from './lib/sync'
import { requestPersistentStorage } from './lib/local'
import { Layout } from './components/Layout'
import { UpdatePrompt } from './components/UpdatePrompt'
import { Empty, Spinner } from './components/ui'
import { Login } from './pages/Login'
import { Home } from './pages/Home'

// Larger screens load on demand so the first paint on a phone stays fast.
const Projects = lazy(() => import('./pages/Projects'))
const ProjectSetup = lazy(() => import('./pages/ProjectSetup'))
const DprList = lazy(() => import('./pages/DprList'))
const DprWizard = lazy(() => import('./pages/DprWizard'))
const DprView = lazy(() => import('./pages/DprView'))
const Activities = lazy(() => import('./pages/Activities'))
const Schedule = lazy(() => import('./pages/Schedule'))
const Manpower = lazy(() => import('./pages/Manpower'))
const Equipment = lazy(() => import('./pages/Equipment'))
const Materials = lazy(() => import('./pages/Materials'))
const SafetyQa = lazy(() => import('./pages/SafetyQa'))
const Documents = lazy(() => import('./pages/Documents'))
const Dashboards = lazy(() => import('./pages/Dashboards'))
const Approvals = lazy(() => import('./pages/Approvals'))
const Settings = lazy(() => import('./pages/Settings'))

function NeedsProject({ children }: { children: ReactNode }) {
  const { project, loading } = useProject()
  if (loading) return <Spinner />
  if (!project) return <Empty title="Select a project first"><Link className="btn btn-primary mt-2" to="/projects">Choose project</Link></Empty>
  return <>{children}</>
}

function Shell() {
  const { session, loading } = useAuth()
  useTheme()
  useEffect(() => {
    if (session) {
      sync.start()
      void requestPersistentStorage()
    }
  }, [session])
  if (loading) return <Spinner label="Starting…" />
  if (!session) return <Login />
  const P = (el: ReactNode) => <NeedsProject>{el}</NeedsProject>
  return (
    <ProjectProvider>
      <Layout>
        <Suspense fallback={<Spinner />}>
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/projects" element={<Projects />} />
            <Route path="/projects/setup" element={P(<ProjectSetup />)} />
            <Route path="/dpr" element={P(<DprList />)} />
            <Route path="/dpr/new" element={P(<DprWizard />)} />
            <Route path="/dpr/edit/:id" element={P(<DprWizard />)} />
            <Route path="/dpr/:id" element={P(<DprView />)} />
            <Route path="/activities" element={P(<Activities />)} />
            <Route path="/schedule" element={P(<Schedule />)} />
            <Route path="/manpower" element={P(<Manpower />)} />
            <Route path="/equipment" element={P(<Equipment />)} />
            <Route path="/materials" element={P(<Materials />)} />
            <Route path="/safety-qa" element={P(<SafetyQa />)} />
            <Route path="/documents" element={P(<Documents />)} />
            <Route path="/dashboards" element={P(<Dashboards />)} />
            <Route path="/approvals" element={P(<Approvals />)} />
            <Route path="/settings" element={<Settings />} />
            <Route path="*" element={<Empty title="Page not found"><Link to="/" className="underline">Go home</Link></Empty>} />
          </Routes>
        </Suspense>
      </Layout>
      <UpdatePrompt />
    </ProjectProvider>
  )
}

export default function App() {
  return (
    <AuthProvider>
      <Shell />
    </AuthProvider>
  )
}
