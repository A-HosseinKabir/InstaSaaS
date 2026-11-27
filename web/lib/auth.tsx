'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { tokenStore } from './api';
import type { Role } from './types';

interface Claims { sub: string; tid: string; role: Role }
interface AuthState {
  ready: boolean;
  token: string | null;
  claims: Claims | null;
  isOwner: boolean;
  canEdit: boolean;            // Owner or Admin
  login: (token: string) => void;
  logout: () => void;
}

const Ctx = createContext<AuthState | null>(null);

function decode(token: string | null): Claims | null {
  if (!token) return null;
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const json = decodeURIComponent(
      atob(payload).split('').map((c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0')).join(''),
    );
    const p = JSON.parse(json);
    if (!p.sub || !p.tid || !p.role) return null;
    if (p.exp && p.exp * 1000 < Date.now()) return null;
    return { sub: p.sub, tid: p.tid, role: p.role };
  } catch { return null; }
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [token, setToken] = useState<string | null>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    const t = tokenStore.get();
    if (t && decode(t)) setToken(t); else tokenStore.clear();
    setReady(true);
    const onUnauth = () => setToken(null);
    window.addEventListener('ps-unauthorized', onUnauth);
    return () => window.removeEventListener('ps-unauthorized', onUnauth);
  }, []);

  const login = useCallback((t: string) => { tokenStore.set(t); setToken(t); }, []);
  const logout = useCallback(() => { tokenStore.clear(); setToken(null); }, []);

  const value = useMemo<AuthState>(() => {
    const claims = decode(token);
    return {
      ready, token, claims,
      isOwner: claims?.role === 'Owner',
      canEdit: claims?.role === 'Owner' || claims?.role === 'Admin',
      login, logout,
    };
  }, [ready, token, login, logout]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth(): AuthState {
  const v = useContext(Ctx);
  if (!v) throw new Error('useAuth must be used inside AuthProvider');
  return v;
}
