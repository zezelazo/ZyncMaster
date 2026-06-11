import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';

// Landing of the emailed magic link (/zync-web/auth/callback?handle&nonce — fixed path issued
// by the server, task 10). Redeems the one-time handle and forwards to the calendar.
@Component({
  selector: 'zw-auth-callback',
  imports: [],
  templateUrl: './auth-callback.component.html',
  styleUrl: './auth-callback.component.css',
})
export class AuthCallbackComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const q = this.route.snapshot.queryParamMap;
    const handle = q.get('handle') ?? '';
    const nonce = q.get('nonce') ?? '';
    if (!handle) { this.error.set('This sign-in link is invalid.'); return; }
    try {
      const ok = await this.auth.redeemHandle(handle, nonce);
      if (ok) { await this.router.navigateByUrl('/calendar'); return; }
      this.error.set('This link belongs to another browser session. Request a new one.');
    } catch {
      this.error.set('This sign-in link expired or was already used. Request a new one.');
    }
  }
}
