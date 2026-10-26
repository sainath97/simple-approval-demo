export type Role = 'Employee' | 'Manager';
export interface DemoToken { accessToken: string; userId: string; displayName: string; role: Role; }
export interface RequestItem {
  id: string; requesterId: string; title: string; description: string; amount: number; status: string;
  createdUtc: string; decidedBy?: string | null; decidedAtUtc?: string | null; version: string;
}
export interface PageResponse<T> { items: T[]; nextCursor?: string | null; }
export interface CreateRequest { title: string; description: string; amount: number; }
