import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import type { Session, User } from '@supabase/supabase-js'
import { supabase } from './supabase'

interface Profile { id: string; full_name: string; email: string | null; phone: string | null; designation: string | null }
interface AuthCtx { session: Session | null; user: User | null; profile: Profile | null; loading: boolean; signOut: () => Promise<void>; reloadProfile: () => Promise<void> }

const Ctx = createContext<AuthCtx>(null!)
export const useAuth = () => useContext(Ctx)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null)
  const [profile, setProfile] = useState<Profile | null>(null)
  const [loading, setLoading] = useState(true)

  const reloadProfile = async (uid?: string) => {
    const id = uid ?? session?.user.id
    if (!id) return setProfile(null)
    const { data } = await supabase.from('profiles').select('*').eq('id', id).maybeSingle()
    setProfile(data)
  }

  useEffect(() => {
    supabase.auth.getSession().then(({ data }) => {
      setSession(data.session)
      setLoading(false)
      if (data.session) void reloadProfile(data.session.user.id)
    })
    const { data: sub } = supabase.auth.onAuthStateChange((_e, s) => {
      setSession(s)
      if (s) void reloadProfile(s.user.id)
      else setProfile(null)
    })
    return () => sub.subscription.unsubscribe()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <Ctx.Provider value={{
      session, user: session?.user ?? null, profile, loading,
      signOut: async () => { await supabase.auth.signOut() },
      reloadProfile: () => reloadProfile(),
    }}>
      {children}
    </Ctx.Provider>
  )
}
