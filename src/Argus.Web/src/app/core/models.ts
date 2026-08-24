/** Mirrors Argus.Server.Capture.QualityLevel - the numeric values are the wire contract. */
export enum QualityLevel {
  Preview = 0,
  Low = 1,
  Medium = 2,
  High = 3,
}

/** Mirrors Argus.Server.Capture.WindowStatus. */
export enum WindowStatus {
  Starting = 0,
  Streaming = 1,
  Stalled = 2,
  Minimised = 3,
  Closed = 4,
}

/** Mirrors Argus.Server.Input.InjectionMode. */
export enum InjectionMode {
  Background = 0,
  Focus = 1,
}

export interface WindowListItem {
  windowId: string;
  handle: number;
  title: string;
  processName: string;
  className: string;
  width: number;
  height: number;
  isConsole: boolean;
  supportsBackgroundInput: boolean;
  backgroundInputNote: string | null;
  matchKey: string;
  selected: boolean;
}

export interface WindowStatusUpdate {
  windowId: string;
  handle: number;
  title: string;
  processName: string;
  status: WindowStatus;
  backend: string;
  actualFps: number;
  subscribers: number;
  supportsBackgroundInput: boolean;
  note: string | null;
}

export interface KeyEventDto {
  windowId: string;
  type: 0 | 1; // Down | Up
  code: string;
  key: string;
  ctrl: boolean;
  shift: boolean;
  alt: boolean;
  meta: boolean;
}

/** Mirrors Argus.Server.Input.MouseAction. */
export enum MouseAction {
  Move = 0,
  Down = 1,
  Up = 2,
  Click = 3,
  Scroll = 4,
}

/** Mirrors Argus.Server.Input.MouseButton. */
export enum MouseButton {
  Left = 0,
  Right = 1,
  Middle = 2,
}

export interface MouseEventDto {
  windowId: string;
  action: MouseAction;
  button: MouseButton;
  /** Fraction of the captured frame, 0..1 - not pixels. */
  x: number;
  y: number;
  /** Scroll only: wheel movement in WHEEL_DELTA units, 120 per notch, positive scrolls up. */
  delta?: number;
}

export interface SendKeyResult {
  delivered: boolean;
  backend?: string;
  reason?: string | null;
}

/** Result of ReleaseKeys. Empty `released` means nothing on the host was down. */
export interface ReleaseKeysResult {
  released: string[];
  reason?: string | null;
}

/** Result of CloseWindow / KillWindow. */
export interface CloseResult {
  closed: boolean;
  /** The handle no longer resolves to a window: the app is already gone, so the row is stale. */
  gone?: boolean;
  reason?: string | null;
}

/** One row of the host-side Browse dialog. Mirrors Argus.Server.Services.BrowseEntry. */
export interface BrowseEntry {
  name: string;
  path: string;
  isDirectory: boolean;
}

/** An app the Explorer page can hand a path to. Mirrors Argus.Server.Services.OpenWithApp. */
export interface OpenWithApp {
  key: string;
  label: string;
  handlesFolders: boolean;
  handlesFiles: boolean;
}

export interface BrowseListing {
  /** Empty at the root listing of drives and shortcuts. */
  path: string;
  label: string;
  parent: string | null;
  entries: BrowseEntry[];
  error?: string | null;
}

export interface Frame {
  handle: number;
  sequence: number;
  width: number;
  height: number;
  bytes: Uint8Array;
}

export const QUALITY_LABELS: Record<QualityLevel, string> = {
  [QualityLevel.Preview]: 'Preview',
  [QualityLevel.Low]: 'Low',
  [QualityLevel.Medium]: 'Medium',
  [QualityLevel.High]: 'High',
};

export const STATUS_LABELS: Record<WindowStatus, string> = {
  [WindowStatus.Starting]: 'Starting',
  [WindowStatus.Streaming]: 'Streaming',
  [WindowStatus.Stalled]: 'Stalled',
  [WindowStatus.Minimised]: 'Minimised',
  [WindowStatus.Closed]: 'Not running',
};
