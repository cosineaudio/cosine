import { client } from './client'

export interface User {
  id: string
  username: string
  isAdmin: boolean
  email?: string
}

export interface LoginRequest {
  username: string
  password: string
}

export interface Profile {
  id: string
  username: string
  email?: string | null
  isAdmin: boolean
  createdAt: string
  lastLoginAt?: string | null
  /** Secret used as the "password" by Subsonic clients doing token (non-legacy) auth. */
  subsonicToken: string
}

export const authApi = {
  login(data: LoginRequest): Promise<User> {
    return client.post<User>('/auth/login', data)
  },
  logout(): Promise<void> {
    return client.post<void>('/auth/logout')
  },
  me(): Promise<User> {
    return client.get<User>('/auth/me')
  },
  profile(): Promise<Profile> {
    return client.get<Profile>('/auth/profile')
  },
}
