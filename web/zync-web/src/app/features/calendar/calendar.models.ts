// Wire models of GET /api/calendar/day and friends. The SERVER owns this shape — it mirrors
// the DayAccountDto/DayEventDto/DayReplicaDto records of CalendarV2Endpoints.cs as pinned by
// the backend plan's CalendarDayEndpointTests (verified in P1); if the merged code differs,
// fix it HERE and in the specs only. Notes: event identity = accountId+eventId (two route
// segments, backend decision 1); scope is PascalCase enum text; freshness is a STRING; the v1
// day view emits NO organizerEmail/responseStatus.
export type Freshness = 'live' | 'snapshot_unavailable';

export interface ReplicaInfo {
  linkId: string;
  maskTitle: string;
  destinationAccountId: string;
  destinationCalendarId: string;
  status: 'active' | 'broken' | 'tombstone';
}

export interface DayEvent {
  accountId: string;
  calendarId: string;
  eventId: string;
  stableId: string;
  title: string;
  start: string;
  end: string;
  isAllDay: boolean;
  showAs: string;
  isCancelled: boolean;
  isOrganizer: boolean;
  isReplica: boolean;
  canWrite: boolean;
  replicas: ReplicaInfo[];
}

export interface DayAccount {
  accountId: string;
  email: string;
  kind: 'graph' | 'com';
  scope: 'Read' | 'ReadWrite';
  freshness: Freshness;
  events: DayEvent[];
}

export interface DayView { date: string; accounts: DayAccount[]; }

export type RespondAction = 'cancel' | 'accept' | 'decline' | 'tentative';

export interface EventKey { accountId: string; eventId: string; }
