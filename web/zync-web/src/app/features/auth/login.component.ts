import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'zw-login',
  imports: [FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css',
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  email = '';
  readonly sent = signal(false);
  readonly error = signal<string | null>(null);

  async send(): Promise<void> {
    this.error.set(null);
    const email = this.email.trim();
    if (!email) { this.error.set('Enter your email address.'); return; }
    try {
      await this.auth.requestMagicLink(email);
      this.sent.set(true);
    } catch {
      this.error.set('Could not send the sign-in link. Try again.');
    }
  }
}
