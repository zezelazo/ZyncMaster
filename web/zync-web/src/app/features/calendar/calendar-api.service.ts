import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_BASE } from '../../core/api/api-base';
import { DayView, RespondAction } from './calendar.models';

@Injectable({ providedIn: 'root' })
export class CalendarApiService {
  private readonly http = inject(HttpClient);

  getDay(dateIso: string): Promise<DayView> {
    return firstValueFrom(this.http.get<DayView>(
      `${API_BASE}/api/calendar/day?date=${encodeURIComponent(dateIso)}`));
  }

  // Write-back to the ORIGIN event (calendar-v2 spec §6) using the two-segment event identity
  // {accountId}/{eventId} (backend decision 1): cancel as organizer, or accept/decline/
  // tentative as invitee, with an optional message to the organizer.
  respond(accountId: string, eventId: string, action: RespondAction, message?: string): Promise<unknown> {
    const body: { action: RespondAction; message?: string } = { action };
    const trimmed = (message ?? '').trim();
    if (trimmed) body.message = trimmed;
    return firstValueFrom(this.http.post(
      `${API_BASE}/api/calendar/events/${encodeURIComponent(accountId)}/${encodeURIComponent(eventId)}/respond`, body));
  }

  // Removes a replica: server deletes the Graph event and tombstones the link.
  deleteReplica(linkId: string): Promise<unknown> {
    return firstValueFrom(this.http.delete(
      `${API_BASE}/api/calendar/replicas/${encodeURIComponent(linkId)}`));
  }
}
