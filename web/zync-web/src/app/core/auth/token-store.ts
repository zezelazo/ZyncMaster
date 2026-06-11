import { Injectable, signal } from '@angular/core';

const REFRESH_KEY = 'zw.refresh';

// Access token lives in MEMORY only; the refresh token persists in localStorage (web-ui spec
// §5 — documented tradeoff; the httpOnly-cookie upgrade is deferred and cheap same-origin).
@Injectable({ providedIn: 'root' })
export class TokenStore {
  private readonly accessSignal = signal<string | null>(null);
  readonly access = this.accessSignal.asReadonly();

  get refresh(): string | null {
    try { return localStorage.getItem(REFRESH_KEY); } catch { return null; }
  }

  get signedIn(): boolean { return this.accessSignal() !== null; }

  setSession(accessToken: string, refreshToken: string): void {
    this.accessSignal.set(accessToken);
    try { localStorage.setItem(REFRESH_KEY, refreshToken); } catch { /* storage blocked */ }
  }

  clear(): void {
    this.accessSignal.set(null);
    try { localStorage.removeItem(REFRESH_KEY); } catch { /* storage blocked */ }
  }
}
