import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { CreateRequest, PageResponse, RequestItem } from './models';

const API = 'http://localhost:5080/api';

@Injectable({ providedIn: 'root' })
export class ApiService {
  constructor(private http: HttpClient) {}
  create(dto: CreateRequest, key: string) {
    return this.http.post<{id: string; status: string; createdUtc: string}>(`${API}/requests`, dto,
      { headers: new HttpHeaders({ 'Idempotency-Key': key }) });
  }
  mine(cursor?: string | null) {
    let params = new HttpParams().set('pageSize', 20);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<PageResponse<RequestItem>>(`${API}/requests/mine`, { params });
  }
  managerQueue(cursor?: string | null) {
    let params = new HttpParams().set('pageSize', 20).set('status', 'Pending');
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<PageResponse<RequestItem>>(`${API}/manager/requests`, { params });
  }
  decide(item: RequestItem, decision: 'Approved' | 'Rejected', comment: string) {
    return this.http.post(`${API}/requests/${item.id}/decision`, { decision, comment },
      { headers: new HttpHeaders({ 'If-Match': `"${item.version}"` }) });
  }
}
