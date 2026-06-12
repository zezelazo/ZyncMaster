import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_BASE } from '../api/api-base';
import { TokenStore } from './token-store';

const NONCE_KEY = 'zw.nonce';

// Magic-link auth against the EXISTING identity endpoints (web mode, task 10 server-side):
// request link -> emailed link lands on /zync-web/auth/callback?handle&nonce -> redeem ->
// bearer + rotating refresh. No new auth protocol — the web is just another client.
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tokens = inject(TokenStore);

  async requestMagicLink(email: string): Promise<void> {
    const nonce = this.issueNonce();
    await firstValueFrom(
      this.http.post(`${API_BASE}/identity/magic-link`, { email, web: true, nonce }));
  }

  // Builds the web-mode Microsoft OAuth start URL with a freshly stored nonce. The server
  // carries mode=web through the signed OAuth state and lands the one-time handle back on
  // /zync-web/auth/callback?handle&nonce — the same callback + redeem the magic link uses.
  microsoftSignInUrl(): string {
    const nonce = this.issueNonce();
    return `${API_BASE}/identity/connect/microsoft?mode=web&nonce=${encodeURIComponent(nonce)}`;
  }

  // Full-page navigation on purpose: the OAuth dance leaves the SPA and returns via the
  // server redirect to /zync-web/auth/callback.
  signInWithMicrosoft(): void {
    window.location.assign(this.microsoftSignInUrl());
  }

  private issueNonce(): string {
    const nonce = crypto.randomUUID();
    try { sessionStorage.setItem(NONCE_KEY, nonce); } catch { /* storage blocked */ }
    return nonce;
  }

  // True when the handle redeemed into a session. A nonce mismatch (link opened in another
  // tab/browser than the one that requested it) fails WITHOUT calling the server.
  async redeemHandle(handle: string, nonce: string): Promise<boolean> {
    let stored: string | null = null;
    try { stored = sessionStorage.getItem(NONCE_KEY); } catch { /* storage blocked */ }
    if (!stored || stored !== nonce) return false;

    const res = await firstValueFrom(
      this.http.post<{ accessToken: string; refreshToken: string }>(
        `${API_BASE}/identity/handle/redeem`, { handle }));
    this.tokens.setSession(res.accessToken, res.refreshToken);
    try { sessionStorage.removeItem(NONCE_KEY); } catch { /* storage blocked */ }
    return true;
  }

  // Rotates the refresh token for a fresh access token. False = the session is gone (revoked,
  // replayed or expired) and the store is cleared so the guard sends the user to /login.
  async refresh(): Promise<boolean> {
    const refreshToken = this.tokens.refresh;
    if (!refreshToken) return false;
    try {
      const res = await firstValueFrom(
        this.http.post<{ accessToken: string; newRefreshToken: string }>(
          `${API_BASE}/identity/refresh`, { refreshToken }));
      this.tokens.setSession(res.accessToken, res.newRefreshToken);
      return true;
    } catch {
      this.tokens.clear();
      return false;
    }
  }

  signOut(): void { this.tokens.clear(); }
}
