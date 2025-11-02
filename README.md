# Robot MR Control - VR Teleoperation Application

> **Related Project**: This VR application works with the [teleop_fetch ROS package](../teleop_fetch-AngelsContr) which provides robot-side implementation for teleoperation.

## Overview

Unity-based Mixed Reality application for Meta Quest VR headsets that enables intuitive teleoperation of robots. This is an example implementation developed for controlling **Brewie** robot, but can be adapted for other robotic systems.

## Features

- **Real-time video streaming** from robot camera
- **Head tracking** - robot head follows operator's head movements
- **Arm teleoperation** - natural hand movements control robot arms with inverse kinematics
- **Gripper control** via VR controller buttons
- **Adjustable UI** - movable, resizable, and space-lockable video window
- **Network-based** communication via WebSocket (ROSBridge)

## System Requirements

- **Hardware**: Meta Quest 2/3/Pro VR headset
- **Software**: Unity (Mixed Reality)
- **Network**: Same local network as the robot
- **Robot Requirements**: Robot must have the [teleop_fetch package](../teleop_fetch) installed and running

## Dependencies

- Unity 2021.3 or later
- XR Plugin Management
- XR Interaction Toolkit
- websocket-sharp library
- Meta Quest platform support

## Quick Start Guide

### Prerequisites

1. Ensure the robot is powered on and connected to the network
2. Verify the [teleop_fetch ROS package](../teleop_fetch) is running on the robot
3. Both VR headset and robot must be on the same network

### Usage Instructions

#### 1. Launch Application
- Put on the VR headset
- Start the application on the headset
- Ensure you're on the same network as the robot

#### 2. Connect to Robot
- Enter the robot's IP address and port
- Press "Connect"
- If successful, you should see the video stream from the robot's camera

#### 3. Adjust Video Window
- Move, resize, or lock the video window in space as needed for comfortable viewing

#### 4. Calibrate and Start Control
- **Position your arms**: Place your arms in the same starting pose as the robot
  - Arms slightly bent at elbows
  - Elbows close to your body (matching robot's initial position)
- **Activate control**: Double-press the **X button** on the left controller
- **Control active**: Your head and arm movements now control the robot
  - Head rotation → Robot head rotation
  - Arm movements → Robot arm movements (with kinematic constraints)

#### 5. Gripper Control
- **Close gripper**: Press the far button (index trigger) on the controller
- **Open gripper**: Press the near button (grip button) on the controller

#### 6. Stop Control
- Press **Y button** on the left controller to stop teleoperation

## Network Configuration

The application uses ROSBridge WebSocket protocol to communicate with the robot:
- **Default port**: 9090 (ROSBridge)
- **Protocol**: WebSocket (ws://)
- Configure IP address and port in the application settings

## Project Structure

```
Assets/
├── Scripts/
│   ├── QuestRosPoseAndJointsPublisher.cs  # VR tracking data publisher
│   ├── RosbridgeImageSubscriber.cs        # Video stream subscriber
│   ├── SoftHeadFollower.cs                # Head following logic
│   └── ...
├── Prefabs/
│   ├── Robot Image Canvas.prefab          # Video display UI
│   ├── Settings Canvas.prefab             # Connection settings UI
│   └── ...
├── Scenes/
│   └── SampleScene.unity                  # Main scene
└── ...
```

## Key Scripts

- **QuestRosPoseAndJointsPublisher.cs**: Publishes VR headset and controller pose/joint data to ROS topics
- **RosbridgeImageSubscriber.cs**: Subscribes to robot camera feed and displays it in VR
- **SoftHeadFollower.cs**: Implements smooth head tracking logic
- **NumberInput.cs**: Handles numeric input for connection settings
- **DefaultTextValue.cs**: Manages default values for text fields

## Topics Published

- `/quest/poses` - Head and hand positions (geometry_msgs/PoseArray)
- `/quest/joints` - Hand joint states (sensor_msgs/JointState)

## Topics Subscribed

- `/robot/camera/image` - Robot camera feed (sensor_msgs/Image or sensor_msgs/CompressedImage)

## Building for Quest

1. Open project in Unity
2. Switch platform to Android (File → Build Settings)
3. Configure XR settings for Oculus/Meta Quest
4. Set build target to Quest 2/3
5. Build and deploy to headset

## Customization

To adapt this application for different robots:

1. Adjust arm calibration positions in the publisher script
2. Modify inverse kinematics scaling factors
3. Update camera topic names
4. Customize UI elements for robot-specific controls

## Troubleshooting

**No video feed**:
- Check network connection
- Verify ROSBridge is running on robot
- Confirm correct IP address and port

**Robot not responding to movements**:
- Ensure calibration was performed correctly
- Check ROS topics are being published
- Verify [teleop_fetch](../teleop_fetch-AngelsContr) node is running

**Connection fails**:
- Ping robot from a device on the same network
- Check firewall settings
- Verify ROSBridge port (default 9090) is accessible

## Related Projects

- **[teleop_fetch ROS Package](../teleop_fetch-AngelsContr)** - Robot-side ROS implementation for Brewie robot

## License

[Your License Here]

## Authors

[Your Team/Organization]

## Version

1.0.0
