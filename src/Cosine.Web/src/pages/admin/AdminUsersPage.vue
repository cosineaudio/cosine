<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { adminApi, type UserDto, type CreateUserRequest, type UpdateUserRequest } from '../../api/admin'
import SkeletonTable from '../../components/ui/SkeletonTable.vue'
import Spinner from '../../components/ui/Spinner.vue'
import { useDeferredLoading } from '../../composables/useDeferredLoading'

const users = ref<UserDto[]>([])
const loading = ref(true)
const showSkeleton = useDeferredLoading(loading)
const error = ref<string | null>(null)
const toast = ref<{ message: string; type: 'success' | 'error' } | null>(null)

const showCreateModal = ref(false)
const editingUser = ref<UserDto | null>(null)
const deletingUser = ref<UserDto | null>(null)

const formUsername = ref('')
const formPassword = ref('')
const formEmail = ref('')
const formIsAdmin = ref(false)

const editEmail = ref('')
const editIsAdmin = ref(false)
const editPassword = ref('')
const saving = ref(false)

async function fetchUsers() {
  try {
    users.value = await adminApi.getUsers()
    error.value = null
  } catch (e) {
    error.value = 'Failed to load users'
  } finally {
    loading.value = false
  }
}

function showToast(message: string, type: 'success' | 'error') {
  toast.value = { message, type }
  setTimeout(() => { toast.value = null }, 3000)
}

function openCreate() {
  formUsername.value = ''
  formPassword.value = ''
  formEmail.value = ''
  formIsAdmin.value = false
  showCreateModal.value = true
}

async function handleCreate() {
  saving.value = true
  try {
    const data: CreateUserRequest = {
      username: formUsername.value,
      password: formPassword.value,
      email: formEmail.value || undefined,
      isAdmin: formIsAdmin.value,
    }
    await adminApi.createUser(data)
    showCreateModal.value = false
    showToast('User created', 'success')
    await fetchUsers()
  } catch (e) {
    showToast('Failed to create user', 'error')
  } finally {
    saving.value = false
  }
}

function openEdit(user: UserDto) {
  editingUser.value = user
  editEmail.value = user.email || ''
  editIsAdmin.value = user.isAdmin
  editPassword.value = ''
}

async function handleUpdate() {
  if (!editingUser.value) return
  saving.value = true
  try {
    const data: UpdateUserRequest = {
      email: editEmail.value || undefined,
      isAdmin: editIsAdmin.value,
      password: editPassword.value || undefined,
    }
    await adminApi.updateUser(editingUser.value.id, data)
    editingUser.value = null
    showToast('User updated', 'success')
    await fetchUsers()
  } catch (e) {
    showToast('Failed to update user', 'error')
  } finally {
    saving.value = false
  }
}

function openDelete(user: UserDto) {
  deletingUser.value = user
}

async function handleDelete() {
  if (!deletingUser.value) return
  saving.value = true
  try {
    await adminApi.deleteUser(deletingUser.value.id)
    deletingUser.value = null
    showToast('User deleted', 'success')
    await fetchUsers()
  } catch (e) {
    showToast('Failed to delete user', 'error')
  } finally {
    saving.value = false
  }
}

onMounted(fetchUsers)
</script>

<template>
  <div class="mx-auto max-w-5xl space-y-6 p-6">
    <div class="flex items-center justify-between">
      <h1 class="font-display text-2xl font-bold tracking-tight text-text">Users</h1>
      <button
        class="rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
        @click="openCreate"
      >
        Create user
      </button>
    </div>

    <div v-if="toast" :class="[
      'rounded-control px-3 py-2 text-sm',
      toast.type === 'success' ? 'border border-border bg-surface-2 text-text' : 'border border-danger/40 bg-danger/10 text-danger',
    ]">
      {{ toast.message }}
    </div>

    <SkeletonTable
      v-if="showSkeleton"
      :rows="6"
      :headers="['Username', 'Email', 'Source', 'Role', 'Last login', 'Created', '']"
    />
    <template v-else-if="loading" />
    <div v-else-if="error" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ error }}</div>

    <div v-else class="reveal overflow-hidden rounded-control border border-border bg-surface">
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-border text-left">
              <th class="label p-3">Username</th>
              <th class="label p-3">Email</th>
              <th class="label p-3">Source</th>
              <th class="label p-3">Role</th>
              <th class="label p-3">Last login</th>
              <th class="label p-3">Created</th>
              <th class="label w-24 p-3"></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="user in users"
              :key="user.id"
              class="border-b border-border/60 last:border-0 hover:bg-surface-2/40"
            >
              <td class="p-3 font-medium text-text">{{ user.username }}</td>
              <td class="p-3 text-muted">{{ user.email || '-' }}</td>
              <td class="p-3 text-muted whitespace-nowrap">{{ user.source === 'local+oidc' ? 'Local + OIDC' : user.source === 'oidc' ? 'OIDC' : 'Local' }}</td>
              <td class="p-3">
                <span :class="[
                  'rounded-[3px] px-2 py-0.5 text-xs font-medium uppercase tracking-wide',
                  user.isAdmin ? 'bg-accent text-accent-ink' : 'bg-surface-2 text-muted',
                ]">
                  {{ user.isAdmin ? 'Admin' : 'User' }}
                </span>
              </td>
              <td class="p-3 text-muted">
                {{ user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : 'Never' }}
              </td>
              <td class="p-3 text-muted">{{ new Date(user.createdAt).toLocaleDateString() }}</td>
              <td class="p-3">
                <div class="flex gap-3">
                  <button class="text-xs text-muted transition-colors hover:text-text" @click="openEdit(user)">
                    Edit
                  </button>
                  <button class="text-xs text-muted transition-colors hover:text-danger" @click="openDelete(user)">
                    Delete
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-if="users.length === 0" class="p-6 text-center text-sm text-muted">
        No users found.
      </div>
    </div>

    <!-- Create Modal -->
    <div v-if="showCreateModal" class="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="showCreateModal = false" />
      <div class="relative w-full max-w-md rounded-control border border-border bg-surface p-6">
        <h2 class="mb-4 font-display text-lg font-semibold text-text">Create user</h2>
        <form class="space-y-4" @submit.prevent="handleCreate">
          <div>
            <label class="label mb-1.5 block">Username</label>
            <input v-model="formUsername" type="text" required class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
          </div>
          <div>
            <label class="label mb-1.5 block">Password</label>
            <input v-model="formPassword" type="password" required class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
          </div>
          <div>
            <label class="label mb-1.5 block">Email (optional)</label>
            <input v-model="formEmail" type="email" class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
          </div>
          <label class="flex items-center gap-2 text-sm text-text">
            <input v-model="formIsAdmin" type="checkbox" class="accent-white" />
            Admin user
          </label>
          <div class="flex justify-end gap-3">
            <button type="button" class="px-4 py-2 text-sm text-muted transition-colors hover:text-text" @click="showCreateModal = false">
              Cancel
            </button>
            <button type="submit" :disabled="saving" class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink disabled:opacity-50">
              <Spinner v-if="saving" />Create
            </button>
          </div>
        </form>
      </div>
    </div>

    <!-- Edit Modal -->
    <div v-if="editingUser" class="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="editingUser = null" />
      <div class="relative w-full max-w-md rounded-control border border-border bg-surface p-6">
        <h2 class="mb-4 font-display text-lg font-semibold text-text">Edit {{ editingUser.username }}</h2>
        <form class="space-y-4" @submit.prevent="handleUpdate">
          <div>
            <label class="label mb-1.5 block">Email</label>
            <input v-model="editEmail" type="email" class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
          </div>
          <div v-if="editingUser.hasLocalPassword">
            <label class="label mb-1.5 block">New password (leave blank to keep)</label>
            <input v-model="editPassword" type="password" class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent" />
          </div>
          <p v-if="!editingUser.hasLocalPassword" class="text-sm text-muted">This account uses single sign-on. Manage its password at the identity provider.</p>
          <label class="flex items-center gap-2 text-sm text-text">
            <input v-model="editIsAdmin" type="checkbox" class="accent-white" />
            Admin user
          </label>
          <div class="flex justify-end gap-3">
            <button type="button" class="px-4 py-2 text-sm text-muted transition-colors hover:text-text" @click="editingUser = null">
              Cancel
            </button>
            <button type="submit" :disabled="saving" class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink disabled:opacity-50">
              <Spinner v-if="saving" />Save
            </button>
          </div>
        </form>
      </div>
    </div>

    <!-- Delete Confirm Modal -->
    <div v-if="deletingUser" class="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="deletingUser = null" />
      <div class="relative w-full max-w-sm rounded-control border border-border bg-surface p-6">
        <h2 class="mb-2 font-display text-lg font-semibold text-text">Delete user</h2>
        <p class="mb-4 text-sm text-muted">
          Are you sure you want to delete <span class="text-text">{{ deletingUser.username }}</span>? This action cannot be undone.
        </p>
        <div class="flex justify-end gap-3">
          <button class="px-4 py-2 text-sm text-muted transition-colors hover:text-text" @click="deletingUser = null">
            Cancel
          </button>
          <button
            :disabled="saving"
            class="flex items-center gap-2 rounded-control bg-danger px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
            @click="handleDelete"
          >
            <Spinner v-if="saving" />Delete
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
